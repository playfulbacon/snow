using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// A placed accessory (twig, carrot, ...). Pure prop: no voxel involvement.
    /// Parented under its sculpture so localPosition/localRotation are the persisted record.
    /// </summary>
    public class SculptureProp : MonoBehaviour
    {
        public string prefabId;
        public SnowSculpture Sculpture { get; private set; }

        public Vector3 LocalPos => transform.localPosition;
        public Quaternion LocalRot => transform.localRotation;

        public void Attach(SnowSculpture sculpture, string id, Vector3 worldPos, Quaternion worldRot)
        {
            Sculpture = sculpture;
            prefabId = id;
            transform.SetParent(sculpture.transform, true);
            transform.SetPositionAndRotation(worldPos, worldRot);
            sculpture.RegisterProp(this);
            var ball = sculpture.GetComponent<Snowball>();
            if (ball != null) ball.Fix(); // a decorated ball is a sculpture now
        }

        /// <summary>Re-home onto another sculpture (promotion); world pose already set by the caller.</summary>
        public void Reattach(SnowSculpture sculpture)
        {
            if (Sculpture != null) Sculpture.UnregisterProp(this);
            Sculpture = sculpture;
            sculpture.RegisterProp(this);
        }

        public void Remove()
        {
            if (Sculpture != null) Sculpture.UnregisterProp(this);
            Destroy(gameObject);
        }
    }
}
