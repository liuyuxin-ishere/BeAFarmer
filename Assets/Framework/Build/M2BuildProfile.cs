using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Game.Framework.Gameplay;

namespace Game.Framework.Build
{
    /// <summary>
    /// M2-T007 最小构建配置（200 基包）。JSON 复用 <see cref="GameplayStrictJson"/>。
    /// </summary>
    public sealed class M2BuildProfile
    {
        public const string RequiredAppVersion = "0.4.0";
        public const string RequiredBridgeContract = "m2-bridge-0.4";
        public const int RequiredProtoVersion = 1;
        public const int RequiredResVersion = 200;
        public const int RequiredConfigVersion = 200;
        public const string ContentPolicyM2Formal = "M2Formal";

        static readonly HashSet<string> AllowedContentPolicies =
            new HashSet<string>(StringComparer.Ordinal)
            {
                ContentPolicyM2Formal,
            };

        M2BuildProfile(
            string appVersion,
            string bridgeContract,
            int protoVersion,
            int resVersion,
            int configVersion,
            string evidenceRoot,
            string contentPolicy,
            string sourcePath)
        {
            AppVersion = appVersion;
            BridgeContract = bridgeContract;
            ProtoVersion = protoVersion;
            ResVersion = resVersion;
            ConfigVersion = configVersion;
            EvidenceRoot = evidenceRoot;
            ContentPolicy = contentPolicy;
            SourcePath = sourcePath;
        }

        public string AppVersion { get; }
        public string BridgeContract { get; }
        public int ProtoVersion { get; }
        public int ResVersion { get; }
        public int ConfigVersion { get; }
        public string EvidenceRoot { get; }
        public string ContentPolicy { get; }
        public string SourcePath { get; }

        public static M2BuildProfile Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path required", nameof(path));
            string full = Path.GetFullPath(path);
            if (!File.Exists(full))
                throw new FileNotFoundException("M2BuildProfile missing", full);

            string text = File.ReadAllText(full, Encoding.UTF8);
            GameplayStrictJson.Node root = GameplayStrictJson.Parse(text);
            if (!(root is GameplayStrictJson.ObjectNode obj))
                throw new FormatException("profile root must be object");

            string app = RequireString(obj, "appVersion");
            string bridge = RequireString(obj, "bridgeContract");
            int proto = RequireInt(obj, "protoVersion");
            int res = RequireInt(obj, "resVersion");
            int cfg = RequireInt(obj, "configVersion");
            string evidence = RequireString(obj, "evidenceRoot");
            string policy = RequireString(obj, "contentPolicy");

            if (!string.Equals(app, RequiredAppVersion, StringComparison.Ordinal))
                throw new FormatException("appVersion must be " + RequiredAppVersion + ", got " + app);
            if (!string.Equals(bridge, RequiredBridgeContract, StringComparison.Ordinal))
                throw new FormatException("bridgeContract must be " + RequiredBridgeContract + ", got " + bridge);
            if (proto != RequiredProtoVersion)
                throw new FormatException("protoVersion must be " + RequiredProtoVersion);
            if (res != RequiredResVersion)
                throw new FormatException("resVersion must be " + RequiredResVersion);
            if (cfg != RequiredConfigVersion)
                throw new FormatException("configVersion must be " + RequiredConfigVersion);
            if (!AllowedContentPolicies.Contains(policy))
                throw new FormatException("contentPolicy not allowed for M2: " + policy);

            string evidenceFull = Path.IsPathRooted(evidence)
                ? Path.GetFullPath(evidence)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full) ?? ".", evidence));
            RejectM1EvidenceRoot(evidenceFull);

            return new M2BuildProfile(app, bridge, proto, res, cfg, evidenceFull, policy, full);
        }

        static void RejectM1EvidenceRoot(string evidenceRoot)
        {
            string n = evidenceRoot.Replace('\\', '/');
            if (n.IndexOf("/Docs/Acceptance/M1/", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("/Docs/Acceptance/xLuaMigration", StringComparison.OrdinalIgnoreCase) >= 0
                || n.EndsWith("/Docs/Acceptance/M1", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf("Acceptance/M1/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new FormatException("EvidenceRoot must not be M1 Accept root: " + evidenceRoot);
            }
        }

        static string RequireString(GameplayStrictJson.ObjectNode obj, string key)
        {
            if (!obj.Fields.TryGetValue(key, out GameplayStrictJson.Node node))
                throw new FormatException("missing field: " + key);
            if (node is GameplayStrictJson.StringNode s)
            {
                if (string.IsNullOrWhiteSpace(s.Value))
                    throw new FormatException("missing field: " + key);
                return s.Value;
            }

            throw new FormatException("field must be string: " + key);
        }

        static int RequireInt(GameplayStrictJson.ObjectNode obj, string key)
        {
            if (!obj.Fields.TryGetValue(key, out GameplayStrictJson.Node node))
                throw new FormatException("missing field: " + key);
            string lit;
            if (node is GameplayStrictJson.NumberNode n)
                lit = n.Literal;
            else if (node is GameplayStrictJson.StringNode s)
                lit = s.Value;
            else
                throw new FormatException("field not int: " + key);
            if (!int.TryParse(lit, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                throw new FormatException("field not int: " + key);
            return value;
        }
    }
}
