using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.UI
{
    public static class ArtLoginBuilder
    {
        public static GameObject Build(Transform parent)
        {
            var root = Box(parent, "LoginWindow", Vector2.zero, Vector2.one);
            var skin = ArtUiSkin.Load();
            var bg = Box(root.transform, "Background", Vector2.zero, Vector2.one);
            var bgImage = bg.AddComponent<Image>(); bgImage.sprite = skin != null ? skin.Find("U01_LoginBackground") : null;
            bgImage.raycastTarget = false;
            var fitter = bg.AddComponent<AspectRatioFitter>(); fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent; fitter.aspectRatio = 16f / 9f;
            var panel = Box(root.transform, "Card", new Vector2(.62f,.17f), new Vector2(.93f,.83f));
            var pi = panel.AddComponent<Image>(); pi.sprite = skin != null ? skin.Find("panel_card") : null; pi.type = Image.Type.Sliced; pi.pixelsPerUnitMultiplier = 2;
            Label(root.transform,"Title","登录",new Vector2(.66f,.68f),new Vector2(.89f,.77f),36);
            Label(root.transform,"Welcome","欢迎回到云岛",new Vector2(.66f,.62f),new Vector2(.89f,.68f),18);
            Input(root.transform,"Username","用户名",new Vector2(.66f,.51f),new Vector2(.89f,.59f));
            Input(root.transform,"Password","密码",new Vector2(.66f,.40f),new Vector2(.89f,.48f));
            Label(root.transform,"ErrorLabel","",new Vector2(.66f,.32f),new Vector2(.89f,.39f),18);
            Button(root.transform,"RegisterButton","注册",new Vector2(.66f,.23f),new Vector2(.765f,.31f));
            Button(root.transform,"LoginButton","登录",new Vector2(.785f,.23f),new Vector2(.89f,.31f));
            ArtUiSkin.Apply(root);
            return root;
        }
        static GameObject Box(Transform parent,string name,Vector2 min,Vector2 max)
        {
            var go=new GameObject(name,typeof(RectTransform)); if(parent!=null) go.transform.SetParent(parent,false);
            var rt=(RectTransform)go.transform; rt.anchorMin=min;rt.anchorMax=max;rt.offsetMin=rt.offsetMax=Vector2.zero;
            return go;
        }
        static Text Label(Transform parent,string name,string text,Vector2 min,Vector2 max,int size)
        {
            var t=Box(parent,name,min,max).AddComponent<Text>();t.font=ArtUiSkin.Load()?.chineseFont ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text=text;t.fontSize=size;t.alignment=TextAnchor.MiddleCenter;t.raycastTarget=false;return t;
        }
        static void Button(Transform parent,string name,string title,Vector2 min,Vector2 max)
        {
            var go=Box(parent,name,min,max);go.AddComponent<Image>();go.AddComponent<Button>();Label(go.transform,"Text",title,Vector2.zero,Vector2.one,22);
        }
        static void Input(Transform parent,string name,string placeholder,Vector2 min,Vector2 max)
        {
            var go=Box(parent,name,min,max);go.AddComponent<Image>();var field=go.AddComponent<InputField>();
            field.textComponent=Label(go.transform,"Text","",new Vector2(.06f,.1f),new Vector2(.94f,.9f),22);
            field.textComponent.alignment=TextAnchor.MiddleLeft;field.textComponent.supportRichText=false;
            field.placeholder=Label(go.transform,"Placeholder",placeholder,new Vector2(.06f,.1f),new Vector2(.94f,.9f),20);
            field.characterLimit=64;
            if(name=="Password")field.contentType=InputField.ContentType.Password;
        }
    }
}
