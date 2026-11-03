using NUnit.Framework;

namespace Snowfield.Net.Tests
{
    /// <summary>
    /// How a join code reads. The grouping is cosmetic, so the thing worth pinning down is that it never leaks
    /// into the code itself: a code read off the screen, typed back in with its space, is the code the service
    /// was given.
    /// </summary>
    public class JoinCodeFormatTests
    {
        [Test]
        public void A_Whole_Code_Reads_As_Two_Groups_Of_Three()
        {
            Assert.AreEqual("8KT QZ4", JoinCodeFormat.Pretty("8KTQZ4"));
        }

        [Test]
        public void An_Empty_Field_Is_All_Dashes()
        {
            Assert.AreEqual("--- ---", JoinCodeFormat.Mask(""));
            Assert.AreEqual("--- ---", JoinCodeFormat.Mask(null));
        }

        [Test]
        public void Typing_Fills_The_Dashes_In_From_The_Left()
        {
            Assert.AreEqual("8-- ---", JoinCodeFormat.Mask("8"));
            Assert.AreEqual("8KT ---", JoinCodeFormat.Mask("8KT"));
            Assert.AreEqual("8KT Q--", JoinCodeFormat.Mask("8KTQ"));
            Assert.AreEqual("8KT QZ4", JoinCodeFormat.Mask("8KTQZ4"));
        }

        [Test]
        public void The_Mask_Is_The_Same_Shape_The_Code_Is_Shown_In()
        {
            Assert.AreEqual(JoinCodeFormat.Pretty("8KTQZ4"), JoinCodeFormat.Mask("8KTQZ4"));
        }

        [Test]
        public void Lower_Case_Is_Taken_And_Raised()
        {
            Assert.AreEqual("8KT QZ4", JoinCodeFormat.Pretty("8ktqz4"));
            Assert.AreEqual("8KT Q--", JoinCodeFormat.Mask("8ktq"));
        }

        [Test]
        public void A_Code_Read_Off_The_Screen_Can_Be_Typed_Back_In_With_Its_Space()
        {
            Assert.AreEqual("8KTQZ4", JoinCodeFormat.Normalize("8KT QZ4"));
            Assert.AreEqual("8KTQZ4", JoinCodeFormat.Normalize("  8kt-qz4 "));
            Assert.AreEqual("", JoinCodeFormat.Normalize("   "));
        }

        [Test]
        public void More_Characters_Than_A_Code_Has_Never_Widen_The_Mask()
        {
            Assert.AreEqual("8KT QZ4", JoinCodeFormat.Mask("8KTQZ4XY"));
        }

        [Test]
        public void Nothing_To_Show_Shows_Nothing()
        {
            Assert.AreEqual("", JoinCodeFormat.Pretty(""));
            Assert.AreEqual("", JoinCodeFormat.Pretty(null));
        }
    }
}
