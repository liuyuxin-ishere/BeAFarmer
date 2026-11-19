using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.UI
{
    public sealed class ArtUiBindings : MonoBehaviour
    {
        public ArtUiSkin skin;
        public void SetIcon(string path, string key)
        {
            var target = transform.Find(path);
            var image = target != null ? target.GetComponent<Image>() : null;
            if (image == null) return;
            // An empty selection hides the icon. An unrecognized item gets the shared placeholder.
            image.sprite = skin != null && !string.IsNullOrEmpty(key)
                ? skin.Find(key) ?? skin.Find("empty")
                : null;
            image.enabled = image.sprite != null;
        }
    }
}
