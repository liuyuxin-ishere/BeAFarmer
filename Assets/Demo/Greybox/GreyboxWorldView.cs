using UnityEngine;

namespace Game.Demo.Greybox
{
    public sealed class GreyboxClickable : MonoBehaviour
    {
        public string TargetId;
    }

    /// <summary>运行时搭建 3×3 地块 / 磨坊 / 订单板，并刷新颜色与高亮。</summary>
    public sealed class GreyboxWorldView : MonoBehaviour
    {
        public Transform[] Plots = new Transform[9];
        public Transform Facility;
        public Transform OrderBoard;
        public Transform Marker;
        GreyboxPlayerMirror _mirror;
        string _highlight = "Plot_0";

        public void Build(GreyboxPlayerMirror mirror)
        {
            _mirror = mirror;
            var root = new GameObject("GreyboxWorld").transform;
            root.SetParent(transform, false);

            // ground
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(root, false);
            ground.transform.localScale = new Vector3(2.2f, 1f, 2.2f);
            SetColor(ground, new Color(0.35f, 0.45f, 0.3f));

            for (int i = 0; i < 9; i++)
            {
                int row = i / 3;
                int col = i % 3;
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Plot_{i}";
                go.transform.SetParent(root, false);
                go.transform.localPosition = new Vector3((col - 1) * 1.4f, 0.25f, (1 - row) * 1.4f);
                go.transform.localScale = new Vector3(1.1f, 0.4f, 1.1f);
                go.AddComponent<GreyboxClickable>().TargetId = go.name;
                Plots[i] = go.transform;
            }

            var mill = GameObject.CreatePrimitive(PrimitiveType.Cube);
            mill.name = "Facility_Mill";
            mill.transform.SetParent(root, false);
            mill.transform.localPosition = new Vector3(4.2f, 0.6f, 0f);
            mill.transform.localScale = new Vector3(1.2f, 1.2f, 1.2f);
            mill.AddComponent<GreyboxClickable>().TargetId = mill.name;
            Facility = mill.transform;
            SetColor(mill, new Color(0.25f, 0.45f, 0.85f));

            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = "OrderBoard";
            board.transform.SetParent(root, false);
            board.transform.localPosition = new Vector3(0f, 0.8f, 4.2f);
            board.transform.localScale = new Vector3(2f, 1.4f, 0.25f);
            board.AddComponent<GreyboxClickable>().TargetId = board.name;
            OrderBoard = board.transform;
            SetColor(board, new Color(0.55f, 0.35f, 0.2f));

            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "GreyboxWorldMarker";
            marker.transform.SetParent(root, false);
            marker.transform.localScale = Vector3.one * 0.35f;
            Object.Destroy(marker.GetComponent<Collider>());
            SetColor(marker, Color.yellow);
            Marker = marker.transform;

            if (Camera.main != null)
            {
                Camera.main.transform.position = new Vector3(2.5f, 8f, -8f);
                Camera.main.transform.rotation = Quaternion.Euler(45f, -20f, 0f);
            }
        }

        public void SetHighlight(string targetId)
        {
            _highlight = targetId ?? "";
            RefreshVisuals();
        }

        public void RefreshVisuals()
        {
            if (_mirror == null) return;
            for (int i = 0; i < Plots.Length; i++)
            {
                if (Plots[i] == null) continue;
                Color c = _mirror.PlotStates[i] switch
                {
                    PlotVisualState.Growing => new Color(0.75f, 0.7f, 0.25f),
                    PlotVisualState.Ripe => new Color(0.25f, 0.8f, 0.3f),
                    _ => new Color(0.55f, 0.55f, 0.55f)
                };
                SetColor(Plots[i].gameObject, c);
            }

            if (Facility != null)
            {
                Color fc = _mirror.SlotState switch
                {
                    SlotVisualState.Working => new Color(0.9f, 0.6f, 0.2f),
                    SlotVisualState.Done => new Color(0.3f, 0.9f, 0.9f),
                    _ => new Color(0.25f, 0.45f, 0.85f)
                };
                SetColor(Facility.gameObject, fc);
            }

            Transform target = FindTarget(_highlight);
            if (Marker != null && target != null)
                Marker.position = target.position + Vector3.up * 1.2f;
        }

        public Transform FindTarget(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.StartsWith("Plot_") && int.TryParse(id.Substring(5), out int idx)
                && idx >= 0 && idx < Plots.Length)
                return Plots[idx];
            if (id == "Facility_Mill") return Facility;
            if (id == "OrderBoard") return OrderBoard;
            return null;
        }

        static void SetColor(GameObject go, Color c)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            r.material = new Material(Shader.Find("Universal Render Pipeline/Unlit")
                                      ?? Shader.Find("Unlit/Color")
                                      ?? Shader.Find("Standard"));
            if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", c);
            r.material.color = c;
        }
    }
}
