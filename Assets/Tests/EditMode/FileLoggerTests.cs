using System;
using System.IO;
using System.Linq;
using CloudIsle.Diagnostics;
using Game.Framework.Core;
using NUnit.Framework;

namespace Game.Framework.EditModeTests
{
    public sealed class FileLoggerTests
    {
        string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "CloudIsle.T12." + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }

        [Test]
        public void Roll_KeepsOnlyRetainedFiles()
        {
            var logger = new FileLogger();
            logger.Initialize(_directory, 80, 2);
            for (int i = 0; i < 4; i++)
                logger.Write(LogLevel.Info, new string('A', 90) + i, Sample());
            logger.Dispose();

            string[] names = Directory.GetFiles(_directory, "client*.log")
                .Select(Path.GetFileName)
                .OrderBy(name => name)
                .ToArray();

            Assert.That(names.Length, Is.LessThanOrEqualTo(2));
            Assert.That(File.Exists(Path.Combine(_directory, "client.2.log")), Is.False);
            string all = string.Concat(names.Select(name => File.ReadAllText(Path.Combine(_directory, name))));
            Assert.That(all, Does.Contain(new string('A', 90) + "3"));
            Assert.That(all, Does.Not.Contain(new string('A', 90) + "0"));
        }

        [Test]
        public void Dispose_LeavesReadableFile()
        {
            var logger = new FileLogger();
            logger.Initialize(_directory, 4096, 2);
            logger.Write(LogLevel.Info, "boot-ok", new LogContext(7, 1005, 3, 9, 12.5d));
            logger.Dispose();

            string text = File.ReadAllText(Path.Combine(_directory, "client.log"));
            Assert.That(text, Does.Contain("INFO"));
            Assert.That(text, Does.Contain("playerId=7"));
            Assert.That(text, Does.Contain("boot-ok"));
        }

        [Test]
        public void Write_RedactsPasswordAndToken()
        {
            var logger = new FileLogger();
            logger.Initialize(_directory, 4096, 1);
            logger.Write(LogLevel.Warn, "login password=secret123 token:abc.def", Sample());
            logger.Dispose();

            string text = File.ReadAllText(Path.Combine(_directory, "client.log"));
            Assert.That(text, Does.Contain("password=***"));
            Assert.That(text, Does.Contain("token:***"));
            Assert.That(text, Does.Not.Contain("secret123"));
            Assert.That(text, Does.Not.Contain("abc.def"));
        }

        [Test]
        public void Write_IncludesNumericContextOnly()
        {
            var logger = new FileLogger();
            logger.Initialize(_directory, 4096, 1);
            logger.Write(LogLevel.Info, "heartbeat", new LogContext(1, 1005, 3, 9, 12.5d));
            logger.Dispose();

            string text = File.ReadAllText(Path.Combine(_directory, "client.log"));
            Assert.That(text, Does.Contain("playerId=1"));
            Assert.That(text, Does.Contain("msgId=1005"));
            Assert.That(text, Does.Contain("seq=3"));
            Assert.That(text, Does.Contain("opId=9"));
            Assert.That(text, Does.Contain("elapsedMs=12.5"));
        }

        static LogContext Sample()
        {
            return new LogContext(1, 1001, 1, 1, 1d);
        }
    }
}
