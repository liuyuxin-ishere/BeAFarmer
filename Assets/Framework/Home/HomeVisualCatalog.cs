using System;
using UnityEngine;

namespace Game.Framework.Home
{
    public sealed class HomeVisualCatalog : ScriptableObject
    {
        [Serializable] public struct Entry { public string key; public GameObject prefab; }
        public Entry[] entries = Array.Empty<Entry>();
        public GameObject plotRoot;
        public GameObject nodeRoot;
        public Material indicatorMaterial;
        public GameObject Find(string key)
        {
            foreach(var e in entries) if(e.key==key) return e.prefab;
            return null;
        }
        public GameObject Spawn(string key,Transform parent)
        {
            var prefab=Find(key);
            if(prefab==null) throw new InvalidOperationException("Home visual missing: "+key);
            var go=Instantiate(prefab,parent,false);go.name=key;return go;
        }
    }
}
