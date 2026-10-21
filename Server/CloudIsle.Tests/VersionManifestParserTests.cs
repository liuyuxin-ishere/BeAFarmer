using System.Text;
using CloudIsle.Contracts;

namespace CloudIsle.Tests
{
    public sealed class VersionManifestParserTests
    {
        [Test]
        public void Parse_Valid_ReturnsFields()
        {
            byte[] bytes = Encoding.UTF8.GetBytes(ValidJson());
            VersionManifest m = VersionManifestParser.Parse(bytes);
            Assert.That(m.MinAppVersion, Is.EqualTo("0.2.0"));
            Assert.That(m.ResVersion, Is.EqualTo(100));
            Assert.That(m.ProtoVersion, Is.EqualTo(1));
            Assert.That(m.ConfigVersion, Is.EqualTo(100));
            Assert.That(m.PackageVersion, Is.EqualTo("2"));
            Assert.That(m.BaseUrl, Is.EqualTo("http://127.0.0.1:18080/100/"));
        }

        [Test]
        public void Parse_RejectsBadInputs()
        {
            Assert.Throws<VersionManifestException>(() => VersionManifestParser.Parse(null!));
            Assert.Throws<VersionManifestException>(() =>
                VersionManifestParser.Parse(Encoding.UTF8.GetBytes("{")));
            Assert.Throws<VersionManifestException>(() =>
                VersionManifestParser.Parse(Encoding.UTF8.GetBytes(ValidJson().Replace("\"ProtoVersion\": 1", "\"ProtoVersion\": 2"))));
            Assert.Throws<VersionManifestException>(() =>
                VersionManifestParser.Parse(Encoding.UTF8.GetBytes(ValidJson().Replace("\"MinAppVersion\": \"0.2.0\"", "\"MinAppVersion\": \"1.2.x\""))));
            Assert.Throws<VersionManifestException>(() =>
                VersionManifestParser.Parse(Encoding.UTF8.GetBytes(ValidJson().Replace("\"BaseUrl\": \"http://127.0.0.1:18080/100/\"", "\"BaseUrl\": \"ftp://x\""))));
            Assert.Throws<VersionManifestException>(() =>
                VersionManifestParser.Parse(Encoding.UTF8.GetBytes(ValidJson().Replace(",\n  \"PackageVersion\": \"2\"", ""))));

            var oversized = new byte[VersionManifestParser.MaxBytes + 1];
            for (int i = 0; i < oversized.Length; i++)
                oversized[i] = (byte)' ';
            Assert.Throws<VersionManifestException>(() => VersionManifestParser.Parse(oversized));
        }

        static string ValidJson()
        {
            return
@"{
  ""MinAppVersion"": ""0.2.0"",
  ""ResVersion"": 100,
  ""ProtoVersion"": 1,
  ""ConfigVersion"": 100,
  ""PackageVersion"": ""2"",
  ""BaseUrl"": ""http://127.0.0.1:18080/100/""
}";
        }
    }
}
