using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CloudIsle.Server.Account;
using CloudIsle.Server.Data;
using Microsoft.Data.Sqlite;

namespace CloudIsle.Tests;

/// <summary>
/// TokenService 的发放、校验、撤销和失败回滚。断言只用布尔、计数和错误码。
/// </summary>
public sealed class TokenServiceTests
{
    const long ExpiresAt = 5_000;

    /// <summary>多次随机发放互不相同，库存只有 SHA-256 摘要。</summary>
    [Test]
    public async Task RandomIssues_AreDistinctAndStoreOnlyDigest()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var service = new TokenService(store);
        long[] players = new long[4];
        string[] tokens = new string[players.Length];
        for (int i = 0; i < players.Length; i++)
        {
            players[i] = await AddPlayer(store, "player-" + i.ToString(CultureInfo.InvariantCulture));
            tokens[i] = await service.IssueAsync(players[i], ExpiresAt, CancellationToken.None);
        }

        Assert.That(AllDistinct(tokens), Is.True);
        Assert.That(PasswordHasher.IterationCount, Is.EqualTo(600_000));
        using var connection = Open(DatabasePath(dir));
        List<string> stored = ReadHashes(connection);
        Assert.That(stored.Count, Is.EqualTo(tokens.Length));
        Assert.That(StoredPlaintext(stored, tokens), Is.False);
        Assert.That(StoredDigests(stored, tokens), Is.True);
        Assert.That(CountWhere(connection, "revoked = 0"), Is.EqualTo(tokens.Length));
    }

    /// <summary>发放后立即验证，重建服务并重开库后仍然有效。</summary>
    [Test]
    public async Task Issue_ValidatesAfterNewServiceAndReopen()
    {
        using var dir = new IsolatedDirectory();
        string database = DatabasePath(dir);
        string token;
        long playerId;
        using (var store = OpenStore(dir))
        {
            playerId = await AddPlayer(store, "ada");
            var first = new TokenService(store);
            token = await first.IssueAsync(playerId, ExpiresAt, CancellationToken.None);
            TokenValidation issued = await first.ValidateAsync(token, ExpiresAt - 1, CancellationToken.None);
            Assert.That(issued.Code, Is.EqualTo(TokenValidation.ValidCode));
            Assert.That(issued.PlayerId, Is.EqualTo(playerId));

            var rebuilt = new TokenService(store);
            TokenValidation again = await rebuilt.ValidateAsync(token, ExpiresAt - 1, CancellationToken.None);
            Assert.That(again.Code, Is.EqualTo(TokenValidation.ValidCode));
            Assert.That(again.PlayerId, Is.EqualTo(playerId));
        }

        using var reopenedStore = new SqliteStore();
        reopenedStore.Initialize(database);
        var reopened = new TokenService(reopenedStore);
        TokenValidation restored = await reopened.ValidateAsync(token, ExpiresAt - 1, CancellationToken.None);
        Assert.That(restored.Code, Is.EqualTo(TokenValidation.ValidCode));
        Assert.That(restored.PlayerId, Is.EqualTo(playerId));
    }

    /// <summary>到期前有效，恰到期和到期后都拒绝。</summary>
    [Test]
    public async Task ExpiryBoundary_RejectsAtAndAfterExpiry()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        long playerId = await AddPlayer(store, "ada");
        var service = new TokenService(store);
        string token = await service.IssueAsync(playerId, ExpiresAt, CancellationToken.None);

        TokenValidation before = await service.ValidateAsync(token, ExpiresAt - 1, CancellationToken.None);
        TokenValidation exact = await service.ValidateAsync(token, ExpiresAt, CancellationToken.None);
        TokenValidation after = await service.ValidateAsync(token, ExpiresAt + 1, CancellationToken.None);

        Assert.That(before.Code, Is.EqualTo(TokenValidation.ValidCode));
        Assert.That(before.PlayerId, Is.EqualTo(playerId));
        Assert.That(exact.Code, Is.EqualTo(TokenValidation.InvalidCode));
        Assert.That(exact.PlayerId, Is.EqualTo(0));
        Assert.That(after.Code, Is.EqualTo(TokenValidation.InvalidCode));
        Assert.That(after.PlayerId, Is.EqualTo(0));
    }

    /// <summary>重复撤销只影响指定玩家。</summary>
    [Test]
    public async Task Revoke_IsIdempotentAndLeavesOtherPlayer()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        long firstPlayer = await AddPlayer(store, "ada");
        long secondPlayer = await AddPlayer(store, "bea");
        var service = new TokenService(store);
        string first = await service.IssueAsync(firstPlayer, ExpiresAt, CancellationToken.None);
        string second = await service.IssueAsync(secondPlayer, ExpiresAt, CancellationToken.None);
        await store.ExecuteAsync(connection =>
        {
            using var extra = connection.CreateCommand();
            extra.CommandText = """
                INSERT INTO auth_token (token_hash, player_id, expires_at_ms, revoked)
                VALUES ($tokenHash, $playerId, $expiresAtMs, 0);
                """;
            extra.Parameters.AddWithValue("$tokenHash", new string('c', 64));
            extra.Parameters.AddWithValue("$playerId", firstPlayer);
            extra.Parameters.AddWithValue("$expiresAtMs", ExpiresAt);
            extra.ExecuteNonQuery();
            return 0;
        }, CancellationToken.None);

        await service.RevokePlayerAsync(firstPlayer, CancellationToken.None);
        await service.RevokePlayerAsync(firstPlayer, CancellationToken.None);

        TokenValidation revoked = await service.ValidateAsync(first, ExpiresAt - 1, CancellationToken.None);
        TokenValidation kept = await service.ValidateAsync(second, ExpiresAt - 1, CancellationToken.None);
        Assert.That(revoked.Code, Is.EqualTo(TokenValidation.InvalidCode));
        Assert.That(revoked.PlayerId, Is.EqualTo(0));
        Assert.That(kept.Code, Is.EqualTo(TokenValidation.ValidCode));
        Assert.That(kept.PlayerId, Is.EqualTo(secondPlayer));

        using var connection = Open(DatabasePath(dir));
        Assert.That(CountFor(connection, firstPlayer, revokedOnly: true), Is.EqualTo(2));
        Assert.That(CountFor(connection, firstPlayer, revokedOnly: false), Is.EqualTo(0));
        Assert.That(CountFor(connection, secondPlayer, revokedOnly: false), Is.EqualTo(1));
        Assert.That(CountFor(connection, secondPlayer, revokedOnly: true), Is.EqualTo(0));
    }

    /// <summary>同一玩家再次发放后，旧原文失效，新原文有效。</summary>
    [Test]
    public async Task Reissue_RevokesPreviousToken()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        long playerId = await AddPlayer(store, "ada");
        var service = new TokenService(store);
        string previous = await service.IssueAsync(playerId, ExpiresAt, CancellationToken.None);
        string current = await service.IssueAsync(playerId, ExpiresAt + 10, CancellationToken.None);

        TokenValidation oldResult = await service.ValidateAsync(previous, ExpiresAt - 1, CancellationToken.None);
        TokenValidation newResult = await service.ValidateAsync(current, ExpiresAt - 1, CancellationToken.None);
        Assert.That(SameText(previous, current), Is.False);
        Assert.That(oldResult.Code, Is.EqualTo(TokenValidation.InvalidCode));
        Assert.That(oldResult.PlayerId, Is.EqualTo(0));
        Assert.That(newResult.Code, Is.EqualTo(TokenValidation.ValidCode));
        Assert.That(newResult.PlayerId, Is.EqualTo(playerId));

        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection), Is.EqualTo(2));
        Assert.That(CountWhere(connection, "revoked = 0"), Is.EqualTo(1));
    }

    /// <summary>新摘要插入失败时，旧令牌仍有效且未撤销。</summary>
    [Test]
    public async Task InsertFailure_KeepsPreviousToken()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        long playerId = await AddPlayer(store, "ada");
        var service = new TokenService(store);
        string previous = await service.IssueAsync(playerId, ExpiresAt, CancellationToken.None);
        using (var connection = Open(DatabasePath(dir)))
        {
            Execute(connection, """
                CREATE TRIGGER abort_token BEFORE INSERT ON auth_token
                BEGIN
                  SELECT RAISE(ABORT, 'token-insert-aborted');
                END;
                """);
        }

        SqliteException? error = null;
        try
        {
            await service.IssueAsync(playerId, ExpiresAt + 10, CancellationToken.None);
            Assert.Fail("新摘要插入失败时应抛出数据库异常");
        }
        catch (SqliteException ex)
        {
            error = ex;
        }

        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Message, Does.Contain("token-insert-aborted"));
        TokenValidation stillValid = await service.ValidateAsync(previous, ExpiresAt - 1, CancellationToken.None);
        Assert.That(stillValid.Code, Is.EqualTo(TokenValidation.ValidCode));
        Assert.That(stillValid.PlayerId, Is.EqualTo(playerId));
        using var check = Open(DatabasePath(dir));
        Assert.That(Count(check), Is.EqualTo(1));
        Assert.That(CountWhere(check, "revoked = 0"), Is.EqualTo(1));
    }

    /// <summary>未知、坏格式和空白都是 204，且不带回玩家。</summary>
    [Test]
    public async Task UnknownOrMalformed_Returns204WithoutPlayer()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        long playerId = await AddPlayer(store, "ada");
        var service = new TokenService(store);
        await service.IssueAsync(playerId, ExpiresAt, CancellationToken.None);
        string unknown = new string('a', 64);

        TokenValidation missing = await service.ValidateAsync(unknown, ExpiresAt - 1, CancellationToken.None);
        TokenValidation upper = await service.ValidateAsync(new string('A', 64), ExpiresAt - 1, CancellationToken.None);
        TokenValidation shortToken = await service.ValidateAsync("abc", ExpiresAt - 1, CancellationToken.None);
        TokenValidation empty = await service.ValidateAsync("", ExpiresAt - 1, CancellationToken.None);
        TokenValidation blank = await service.ValidateAsync("   ", ExpiresAt - 1, CancellationToken.None);
        TokenValidation nullToken = await service.ValidateAsync(null!, ExpiresAt - 1, CancellationToken.None);

        AssertRejected(missing);
        AssertRejected(upper);
        AssertRejected(shortToken);
        AssertRejected(empty);
        AssertRejected(blank);
        AssertRejected(nullToken);
    }

    /// <summary>非法玩家编号和外键失败都不返回可用令牌。</summary>
    [Test]
    public async Task IllegalPlayer_DoesNotReturnToken()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        var service = new TokenService(store);

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await service.IssueAsync(0, ExpiresAt, CancellationToken.None));
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await service.IssueAsync(-1, ExpiresAt, CancellationToken.None));

        SqliteException? error = null;
        try
        {
            await service.IssueAsync(424242, ExpiresAt, CancellationToken.None);
            Assert.Fail("不存在的玩家应因外键失败");
        }
        catch (SqliteException ex)
        {
            error = ex;
        }

        Assert.That(error, Is.Not.Null);
        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection), Is.EqualTo(0));
    }

    /// <summary>校验遇到数据库错误时继续抛出，不改写成 204。</summary>
    [Test]
    public async Task DatabaseError_PropagatesFromValidate()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        long playerId = await AddPlayer(store, "ada");
        var service = new TokenService(store);
        string token = await service.IssueAsync(playerId, ExpiresAt, CancellationToken.None);
        await store.ExecuteAsync(connection =>
        {
            Execute(connection, "ALTER TABLE auth_token RENAME TO auth_token_hidden;");
            return 0;
        }, CancellationToken.None);

        SqliteException? error = null;
        try
        {
            await service.ValidateAsync(token, ExpiresAt - 1, CancellationToken.None);
            Assert.Fail("缺少令牌表时应抛出数据库异常");
        }
        catch (SqliteException ex)
        {
            error = ex;
        }

        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Message, Does.Contain("no such table"));
    }

    /// <summary>已入队但尚未执行时取消，不写令牌，也不返回伪造的校验结果。</summary>
    [Test]
    public async Task CancelWhileQueued_WritesNothingAndDoesNotValidate()
    {
        using var dir = new IsolatedDirectory();
        using var store = OpenStore(dir);
        long playerId = await AddPlayer(store, "ada");
        var service = new TokenService(store);
        using var issuedCancel = new CancellationTokenSource();
        issuedCancel.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await service.IssueAsync(playerId, ExpiresAt, issuedCancel.Token));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> hold = store.ExecuteAsync(_ =>
        {
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("排队占用没有被放开");
            return 0;
        }, CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        Task<string> pending = service.IssueAsync(playerId, ExpiresAt, cts.Token);
        cts.Cancel();
        release.TrySetResult();

        Assert.ThrowsAsync<TaskCanceledException>(async () => await pending);
        await hold;
        using var connection = Open(DatabasePath(dir));
        Assert.That(Count(connection), Is.EqualTo(0));

        string token = await service.IssueAsync(playerId, ExpiresAt, CancellationToken.None);
        var validateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validateRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> validateHold = store.ExecuteAsync(_ =>
        {
            validateEntered.TrySetResult();
            if (!validateRelease.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("校验排队占用没有被放开");
            return 0;
        }, CancellationToken.None);
        await validateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var validateCts = new CancellationTokenSource();
        Task<TokenValidation> validation = service.ValidateAsync(token, ExpiresAt - 1, validateCts.Token);
        validateCts.Cancel();
        validateRelease.TrySetResult();

        Assert.ThrowsAsync<TaskCanceledException>(async () => await validation);
        await validateHold;
        TokenValidation after = await service.ValidateAsync(token, ExpiresAt - 1, CancellationToken.None);
        Assert.That(after.Code, Is.EqualTo(TokenValidation.ValidCode));
        Assert.That(after.PlayerId, Is.EqualTo(playerId));
        using var afterConnection = Open(DatabasePath(dir));
        Assert.That(Count(afterConnection), Is.EqualTo(1));
    }

    /// <summary>无效结果必须是 204 且玩家编号为 0。</summary>
    /// <param name="validation">一次校验结果。</param>
    static void AssertRejected(TokenValidation validation)
    {
        Assert.That(validation.Code, Is.EqualTo(TokenValidation.InvalidCode));
        Assert.That(validation.PlayerId, Is.EqualTo(0));
    }

    /// <summary>插入账号和玩家，返回玩家编号。不经过密码派生。</summary>
    /// <param name="store">已经初始化的测试库。</param>
    /// <param name="username">本用例内唯一的用户名。</param>
    static Task<long> AddPlayer(SqliteStore store, string username)
    {
        return store.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using (var account = connection.CreateCommand())
            {
                account.Transaction = transaction;
                account.CommandText = """
                    INSERT INTO account (username, pwd_hash, salt, created_at)
                    VALUES ($username, $pwdHash, $salt, $createdAt);
                    """;
                account.Parameters.AddWithValue("$username", username);
                account.Parameters.AddWithValue("$pwdHash", "not-a-password-hash");
                account.Parameters.AddWithValue("$salt", "not-a-salt");
                account.Parameters.AddWithValue("$createdAt", 1L);
                account.ExecuteNonQuery();
            }

            long accountId;
            using (var id = connection.CreateCommand())
            {
                id.Transaction = transaction;
                id.CommandText = "SELECT last_insert_rowid();";
                accountId = Convert.ToInt64(id.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using var player = connection.CreateCommand();
            player.Transaction = transaction;
            player.CommandText = """
                INSERT INTO player (account_id, nickname)
                VALUES ($accountId, $nickname);
                """;
            player.Parameters.AddWithValue("$accountId", accountId);
            player.Parameters.AddWithValue("$nickname", username);
            player.ExecuteNonQuery();
            using var playerId = connection.CreateCommand();
            playerId.Transaction = transaction;
            playerId.CommandText = "SELECT last_insert_rowid();";
            long idValue = Convert.ToInt64(playerId.ExecuteScalar(), CultureInfo.InvariantCulture);
            transaction.Commit();
            return idValue;
        }, CancellationToken.None);
    }

    /// <summary>原文两两都不相同。失败时只报告布尔值。</summary>
    /// <param name="tokens">本次发放返回的原文。</param>
    static bool AllDistinct(IReadOnlyList<string> tokens)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            for (int j = i + 1; j < tokens.Count; j++)
            {
                if (SameText(tokens[i], tokens[j]))
                    return false;
            }
        }

        return true;
    }

    /// <summary>库存是否出现过任一原文。</summary>
    /// <param name="stored">token_hash 列。</param>
    /// <param name="tokens">发放返回的原文。</param>
    static bool StoredPlaintext(IReadOnlyList<string> stored, IReadOnlyList<string> tokens)
    {
        foreach (string hash in stored)
        {
            foreach (string token in tokens)
            {
                if (SameText(hash, token))
                    return true;
            }
        }

        return false;
    }

    /// <summary>每一份原文的 SHA-256 是否都能在库里找到。</summary>
    /// <param name="stored">token_hash 列。</param>
    /// <param name="tokens">发放返回的原文。</param>
    static bool StoredDigests(IReadOnlyList<string> stored, IReadOnlyList<string> tokens)
    {
        foreach (string token in tokens)
        {
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
            bool found = false;
            foreach (string hash in stored)
            {
                if (SameText(hash, digest))
                    found = true;
            }

            if (!found)
                return false;
        }

        return true;
    }

    /// <summary>等长时做固定时间比较。长度不同直接视为不同，避免把原文写进断言。</summary>
    /// <param name="left">第一段文本。</param>
    /// <param name="right">第二段文本。</param>
    static bool SameText(string left, string right)
    {
        if (left.Length != right.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    }

    /// <summary>打开隔离库并完成现有迁移。</summary>
    /// <param name="dir">本用例的临时目录。</param>
    static SqliteStore OpenStore(IsolatedDirectory dir)
    {
        var store = new SqliteStore();
        store.Initialize(DatabasePath(dir));
        return store;
    }

    /// <summary>返回本用例数据库文件路径。</summary>
    /// <param name="dir">本用例的临时目录。</param>
    static string DatabasePath(IsolatedDirectory dir)
    {
        return Path.Combine(dir.FullPath, "game.db");
    }

    /// <summary>另开一条不进连接池的连接，供计数使用。</summary>
    /// <param name="database">数据库文件路径。</param>
    static SqliteConnection Open(string database)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>执行一段测试夹具 SQL。</summary>
    /// <param name="connection">已经打开的连接。</param>
    /// <param name="sql">触发器或改名语句。</param>
    static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>读取全部摘要文本，调用方只用它做布尔比较。</summary>
    /// <param name="connection">已经打开的连接。</param>
    static List<string> ReadHashes(SqliteConnection connection)
    {
        var hashes = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT token_hash FROM auth_token ORDER BY rowid;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            hashes.Add(reader.GetString(0));
        return hashes;
    }

    /// <summary>统计令牌行数。</summary>
    /// <param name="connection">已经打开的连接。</param>
    static long Count(SqliteConnection connection)
    {
        return CountWhere(connection, "1 = 1");
    }

    /// <summary>按固定条件统计令牌行数。条件只来自测试代码。</summary>
    /// <param name="connection">已经打开的连接。</param>
    /// <param name="where">不含外部输入的条件。</param>
    static long CountWhere(SqliteConnection connection, string where)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM auth_token WHERE " + where + ";";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>统计某玩家已撤销或未撤销的行数。</summary>
    /// <param name="connection">已经打开的连接。</param>
    /// <param name="playerId">玩家编号。</param>
    /// <param name="revokedOnly">true 统计已撤销，false 统计未撤销。</param>
    static long CountFor(SqliteConnection connection, long playerId, bool revokedOnly)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM auth_token
            WHERE player_id = $playerId AND revoked = $revoked;
            """;
        command.Parameters.AddWithValue("$playerId", playerId);
        command.Parameters.AddWithValue("$revoked", revokedOnly ? 1 : 0);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>只删除系统临时目录里本测试创建的子目录。</summary>
    sealed class IsolatedDirectory : IDisposable
    {
        public IsolatedDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "FarmingTokenServiceTests");
            FullPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullPath);
        }

        /// <summary>本用例独占的绝对路径。</summary>
        public string FullPath { get; }

        /// <summary>确认路径仍在指定根目录下后再删除。</summary>
        public void Dispose()
        {
            string full = Path.GetFullPath(FullPath);
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FarmingTokenServiceTests"));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝删除测试目录之外的路径");
            if (Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }
}
