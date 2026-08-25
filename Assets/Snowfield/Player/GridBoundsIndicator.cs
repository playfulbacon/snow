using Snowfield.Sculpture;
using UnityEngine;

namespace Snowfield.Player
{
    /// <summary>
    /// Shows the aimed sculpture's grid bounds as a wire box when the brush gets close to a wall, so the grid
    /// limit reads as "this sculpture is full" instead of a bug. Fades with proximity; hidden otherwise.
    /// Lives on its own root object; finds the SculptTool by itself.
    /// </summary>
    public class GridBoundsIndicator : MonoBehaviour
    {
        public SculptTool tool;
        [Tooltip("Start fading the box in when the brush sphere is within this distance of a wall (m).")]
        public float warnDistance = 0.25f;
        public float edgeThickness = 0.015f;
        public Color color = new Color(0.4f, 0.7f, 1f, 0.55f);

        Transform _root;
        Transform[] _edges;
        Material _mat;
        SnowSculpture _shownFor;

        void Awake()
        {
            if (tool == null) tool = FindAnyObjectByType<SculptTool>();
            Build();
        }

        void Build()
        {
            _root = new GameObject("BoundsBox").transform;
            _root.SetParent(transform, false);
            _mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "GridBounds" };
            _mat.SetFloat("_Surface", 1f);
            _mat.SetOverrideTag("RenderType", "Transparent");
            _mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_ZWrite", 0);
            _mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            _edges = new Transform[12];
            for (int i = 0; i < 12; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Edge" + i;
                Destroy(go.GetComponent<Collider>());
                go.layer = LayerMask.NameToLayer("Ignore Raycast");
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = _mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _edges[i] = go.transform;
                go.transform.SetParent(_root, false);
            }
            _root.gameObject.SetActive(false);
        }

        void LateUpdate()
        {
            if (tool == null || _root == null) return;
            var target = tool.Target;
            bool show = false;
            float alpha = 0f;

            if (target != null && (tool.Mode == ToolMode.Sculpt || tool.Mode == ToolMode.EmptyHand) && tool.HasHit && !tool.HandsFull)
            {
                // Distance from the brush sphere to the nearest wall, in the sculpture's local space.
                Vector3 local = target.transform.InverseTransformPoint(tool.BrushPoint);
                Bounds b = target.LocalBounds;
                float radius = tool.CurrentRadius();
                float wall = float.MaxValue;
                for (int axis = 0; axis < 3; axis++)
                {
                    wall = Mathf.Min(wall, local[axis] - b.min[axis]);
                    wall = Mathf.Min(wall, b.max[axis] - local[axis]);
                }
                wall -= radius;
                if (wall < warnDistance)
                {
                    show = true;
                    alpha = Mathf.Clamp01(1f - wall / warnDistance);
                }
            }

            _root.gameObject.SetActive(show);
            if (!show) { _shownFor = target; return; }

            var c = color; c.a *= alpha;
            _mat.SetColor("_BaseColor", c);
            if (_shownFor != target) Pose(target);
            else Pose(target); // cheap enough; keeps up with regrow/moves
            _shownFor = target;
        }

        void Pose(SnowSculpture s)
        {
            Bounds b = s.LocalBounds;
            _root.SetPositionAndRotation(s.transform.TransformPoint(b.center), s.transform.rotation);
            Vector3 e = b.extents;
            float t = edgeThickness;
            int i = 0;
            // 4 edges along each axis
            for (int a = 0; a < 3; a++)
            {
                Vector3 size = new Vector3(t, t, t);
                size[a] = b.size[a] + t;
                int u = (a + 1) % 3, v = (a + 2) % 3;
                for (int su = -1; su <= 1; su += 2)
                for (int sv = -1; sv <= 1; sv += 2)
                {
                    Vector3 pos = Vector3.zero;
                    pos[u] = e[u] * su;
                    pos[v] = e[v] * sv;
                    _edges[i].localPosition = pos;
                    _edges[i].localRotation = Quaternion.identity;
                    _edges[i].localScale = size;
                    i++;
                }
            }
        }
    }
}
