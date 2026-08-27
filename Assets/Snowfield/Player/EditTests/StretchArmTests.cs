using NUnit.Framework;
using UnityEngine;

namespace Snowfield.Player.Tests
{
    /// <summary>
    /// The stretchy two-bone solver on its own — a plain three-transform chain, no Animator, no humanoid.
    /// Covers the three things the arms depend on: it lands on reachable targets, it lengthens the bones rather
    /// than stopping short, and running it twice on the same pose does not compound the stretch.
    /// </summary>
    public class StretchArmTests
    {
        const float UpperLen = 0.3f;
        const float LowerLen = 0.25f;
        const float Natural = UpperLen + LowerLen;

        Transform _upper, _lower, _hand;
        Vector3 _restLower, _restHand;

        [SetUp]
        public void MakeArm()
        {
            _upper = new GameObject("upper").transform;
            _lower = new GameObject("lower").transform;
            _hand = new GameObject("hand").transform;
            _lower.SetParent(_upper);
            _hand.SetParent(_lower);
            _restLower = new Vector3(0f, 0f, UpperLen);
            _restHand = new Vector3(0f, 0f, LowerLen);
            _lower.localPosition = _restLower;
            _hand.localPosition = _restHand;
        }

        [TearDown]
        public void DropArm() => Object.DestroyImmediate(_upper.gameObject);

        void Solve(Vector3 target, float maxStretch, Vector3 bend = default) =>
            HandRig.Solve(_upper, _lower, _hand, _restLower, _restHand, target,
                bend == default ? Vector3.up : bend, maxStretch);

        float UpperWorldLength => Vector3.Distance(_upper.position, _lower.position);
        float LowerWorldLength => Vector3.Distance(_lower.position, _hand.position);

        [Test]
        public void ReachableTarget_PutsTheHandOnIt_WithoutStretching()
        {
            var target = new Vector3(0.2f, -0.1f, 0.25f); // 0.34 m out, well inside the 0.55 m arm
            Solve(target, 3f);

            Assert.That(Vector3.Distance(_hand.position, target), Is.LessThan(1e-3f), "hand missed the target");
            Assert.That(UpperWorldLength, Is.EqualTo(UpperLen).Within(1e-4f), "upper arm stretched when it did not need to");
            Assert.That(LowerWorldLength, Is.EqualTo(LowerLen).Within(1e-4f), "forearm stretched when it did not need to");
        }

        [Test]
        public void TargetPastTheArm_StretchesBothBonesToReachIt()
        {
            var target = new Vector3(0f, 0f, 1.2f); // more than double the natural arm
            Solve(target, 4f);

            Assert.That(Vector3.Distance(_hand.position, target), Is.LessThan(1e-3f), "the arm should have stretched onto it");
            float stretch = (UpperWorldLength + LowerWorldLength) / Natural;
            Assert.That(stretch, Is.GreaterThan(2f), "bones did not lengthen");
            // Both bones stretch together, so the elbow stays proportionally where it was.
            Assert.That(UpperWorldLength / LowerWorldLength, Is.EqualTo(UpperLen / LowerLen).Within(1e-3f));
        }

        [Test]
        public void PastTheStretchCap_TheArmPointsAtTheTargetAndComesUpShort()
        {
            var target = new Vector3(0f, 0f, 5f);
            Solve(target, 2f);

            float span = UpperWorldLength + LowerWorldLength;
            Assert.That(span, Is.EqualTo(Natural * 2f).Within(1e-3f), "should be capped at 2x, not longer");
            Assert.That(Vector3.Distance(_upper.position, _hand.position), Is.EqualTo(span).Within(1e-2f),
                "a capped arm should be straight");
            // Straight at the target, just not all the way there.
            Vector3 toHand = _hand.position - _upper.position;
            Assert.That(Vector3.Angle(toHand, target - _upper.position), Is.LessThan(1f));
        }

        [Test]
        public void SolvingTwiceOnTheSamePose_DoesNotCompound()
        {
            var target = new Vector3(0.1f, 0.4f, 1f);
            Solve(target, 4f);
            Vector3 once = _hand.position;
            float span = UpperWorldLength + LowerWorldLength;

            Solve(target, 4f);

            Assert.That(Vector3.Distance(_hand.position, once), Is.LessThan(1e-4f), "hand drifted on the second solve");
            Assert.That(UpperWorldLength + LowerWorldLength, Is.EqualTo(span).Within(1e-4f), "stretch compounded");
        }

        [Test]
        public void ElbowFollowsTheBendHint()
        {
            var target = new Vector3(0f, 0f, 0.4f); // bent enough for the elbow to have somewhere to go
            Solve(target, 3f, Vector3.down);

            Assert.That(_lower.position.y, Is.LessThan(-0.05f), "elbow should have dropped toward the hint");

            Solve(target, 3f, Vector3.up);
            Assert.That(_lower.position.y, Is.GreaterThan(0.05f), "elbow should have lifted toward the hint");
        }
    }
}
