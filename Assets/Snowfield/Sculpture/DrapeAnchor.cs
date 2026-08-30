using UnityEngine;

namespace Snowfield.Sculpture
{
    /// <summary>
    /// A clickable point on a chain. Only visible while its chain is still being placed, so the player can click an
    /// existing point to reuse it (closing a scarf into a loop) instead of adding a new one.
    /// Trigger collider, following the same convention as placed props: the aim ray hits it, physics ignores it.
    /// </summary>
    public class DrapeAnchor : MonoBehaviour
    {
        public DrapedAccessory Chain { get; private set; }
        /// <summary>Index into the chain's point list.</summary>
        public int Index { get; private set; }

        public static DrapeAnchor Create(DrapedAccessory chain, int index, Vector3 worldPos, float radius, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Anchor" + index;
            go.transform.SetParent(chain.transform, false);
            go.transform.position = worldPos;
            go.transform.localScale = Vector3.one * radius * 2f;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var col = go.GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
            var anchor = go.AddComponent<DrapeAnchor>();
            anchor.Chain = chain;
            anchor.Index = index;
            return anchor;
        }
    }
}
