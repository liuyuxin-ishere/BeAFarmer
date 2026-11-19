using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.UI
{
    /// <summary>Base-package skin. References are shared by windows; no per-icon async handles.</summary>
    public sealed class ArtUiSkin : ScriptableObject
    {
        [Serializable] public struct Entry { public string key; public Sprite sprite; }
        public Font chineseFont;
        public Entry[] sprites = Array.Empty<Entry>();
        public Sprite Find(string key)
        {
            foreach (var entry in sprites) if (entry.key == key) return entry.sprite;
            return null;
        }
        public static ArtUiSkin Load() => Resources.Load<ArtUiSkin>("CloudIsle/ArtUiSkin_v03");

        public static void Apply(GameObject root)
        {
            var skin = Load();
            if (skin == null) return; // Legacy test/fallback builders before the art bake.
            var binding = root.GetComponent<ArtUiBindings>() ?? root.AddComponent<ArtUiBindings>();
            binding.skin = skin;
            foreach (var text in root.GetComponentsInChildren<Text>(true))
            {
                text.font = skin.chineseFont;
                text.color = new Color(.19f, .25f, .20f);
                text.raycastTarget = false;
                if (text.name == "Title") { text.fontStyle = FontStyle.Bold; text.color = new Color(.20f, .36f, .26f); }
                if (text.name == "ErrorLabel") text.color = new Color(.65f, .22f, .16f);
            }
            foreach (var input in root.GetComponentsInChildren<InputField>(true))
            {
                var image = input.GetComponent<Image>();
                SetImage(image, skin.Find("input_normal"));
                input.transition = Selectable.Transition.SpriteSwap;
                input.spriteState = new SpriteState { selectedSprite = skin.Find("input_focus"), highlightedSprite = skin.Find("input_focus") };
            }
            foreach (var button in root.GetComponentsInChildren<Button>(true))
            {
                SetImage(button.GetComponent<Image>(), skin.Find(button.name == "CloseButton" || button.name == "LogoutButton" || button.name == "RegisterButton" ? "button_secondary" : "button_primary"));
                button.transition = Selectable.Transition.SpriteSwap;
                button.spriteState = new SpriteState { highlightedSprite = skin.Find("button_hover"), pressedSprite = skin.Find("button_pressed"), disabledSprite = skin.Find("button_disabled"), selectedSprite = skin.Find("button_hover") };
                var label = button.GetComponentInChildren<Text>();
                if (label != null)
                {
                    label.fontSize = Mathf.Min(label.fontSize, root.name == "HomeHudWindow" ? 16 : 20);
                    label.resizeTextForBestFit = true;
                    label.resizeTextMinSize = 12;
                    label.resizeTextMaxSize = label.fontSize;
                }
            }
            SetImage(root.GetComponent<Image>(), skin.Find("panel_card"));
            if (root.name == "FarmWindow") AddDisplayIcon(root, "SeedIcon", "2001", new Vector2(.04f,.745f), new Vector2(.19f,.815f), "SeedLabel", .22f);
            if (root.name == "GatherWindow") AddDisplayIcon(root, "SelectedIcon", "gather", new Vector2(.04f,.40f), new Vector2(.20f,.48f), "SelectedLabel", .23f);
            if (root.name == "ToolWindow") AddDisplayIcon(root, "ToolIcon", "level", new Vector2(.05f,.40f), new Vector2(.12f,.48f), "SelectedLabel", .14f);
            if (root.name == "HomeHudWindow")
            {
                foreach (var pair in new[] { "Bag:bag", "Shop:shop", "Farm:farm", "Gather:gather", "Tool:level" })
                {
                    var p = pair.Split(':'); var button = root.transform.Find(p[0] + "Button");
                    if (button == null) continue;
                    var label = button.Find("Text") as RectTransform;
                    if (label != null) label.anchorMax = new Vector2(1, .5f);
                    AddDisplayIcon(button.gameObject, "ArtIcon", p[1], new Vector2(.25f,.47f), new Vector2(.75f,.97f), null, 0);
                }
            }
        }

        static void SetImage(Image image, Sprite sprite)
        {
            if (image == null || sprite == null) return;
            image.sprite = sprite; image.type = Image.Type.Sliced; image.color = Color.white;
            image.pixelsPerUnitMultiplier = 2f;
        }

        static void AddDisplayIcon(GameObject root, string name, string key, Vector2 min, Vector2 max, string labelName, float textLeft)
        {
            var skin = Load();
            var existing = root.transform.Find(name);
            var go = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(root.transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = min; rt.anchorMax = max; rt.offsetMin = rt.offsetMax = Vector2.zero;
            var image = go.GetComponent<Image>(); image.sprite = skin.Find(key); image.preserveAspect = true; image.raycastTarget = false;
            if (labelName != null && root.transform.Find(labelName) is RectTransform label)
                label.anchorMin = new Vector2(textLeft, label.anchorMin.y);
        }
    }
}
