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
            image.sprite = skin != null ? skin.Find(key) : null;
            image.enabled = image.sprite != null; // Missing IDs never retain the previous item's icon.
        }
    }
}
