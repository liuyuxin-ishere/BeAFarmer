using System;
using CloudIsle.Contracts;

namespace CloudIsle.Tests
{
    public sealed class AppVersionComparerTests
    {
        [Test]
        public void Compare_NumericSegments_NotLexicographic()
        {
            Assert.That(AppVersionComparer.Compare("0.1.10", "0.1.9"), Is.GreaterThan(0));
            Assert.That(AppVersionComparer.IsGreater("0.1.10", "0.1.9"), Is.True);
            Assert.That(AppVersionComparer.Compare("0.1.9", "0.1.10"), Is.LessThan(0));
        }

        [Test]
        public void Compare_Baseline020_EqualLowHigh()
        {
            Assert.That(AppVersionComparer.Compare("0.2.0", "0.2.0"), Is.EqualTo(0));
            Assert.That(AppVersionComparer.Compare("0.1.9", "0.2.0"), Is.LessThan(0));
            Assert.That(AppVersionComparer.Compare("0.2.1", "0.2.0"), Is.GreaterThan(0));
            Assert.That(AppVersionComparer.Compare("0.10.0", "0.2.0"), Is.GreaterThan(0));
        }

        [Test]
        public void Compare_MultiDigitAndExtraSegments()
        {
            Assert.That(AppVersionComparer.Compare("1.2.3.4", "1.2.3"), Is.GreaterThan(0));
            Assert.That(AppVersionComparer.Compare("1.2.3", "1.2.3.0"), Is.EqualTo(0));
            Assert.That(AppVersionComparer.Compare("10.0.0", "9.99.99"), Is.GreaterThan(0));
        }

        [Test]
        public void ParseParts_Invalid_Throws()
        {
            Assert.Throws<VersionManifestException>(() => AppVersionComparer.ParseParts("", "v"));
            Assert.Throws<VersionManifestException>(() => AppVersionComparer.ParseParts("1.2.a", "v"));
            Assert.Throws<VersionManifestException>(() => AppVersionComparer.ParseParts("1..2", "v"));
            Assert.Throws<VersionManifestException>(() => AppVersionComparer.ParseParts("-1.0.0", "v"));
        }
    }
}
