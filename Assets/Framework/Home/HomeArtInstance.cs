using UnityEngine;

namespace Game.Framework.Home
{
    /// <summary>Visual-only state. Identity/collision remain on the stable interaction root.</summary>
    public sealed class HomeArtInstance : MonoBehaviour
    {
        HomeVisualCatalog _catalog;
        Transform _visualRoot;
        Transform _cropAnchor;
        GameObject _crop, _lock, _fertilized, _ready, _cooldown;
        Renderer[] _soil;
        LineRenderer _outline;
        MaterialPropertyBlock _block;
        string _cropKey;
        bool _locked;
        int _baseKind, _cropId;
        long _planted, _deadline;
        bool _watered, _fert;
        float _nextRefresh;
        bool _hasTimeline;
        bool _hasNodeTimeline;
        int _nodeBaseKind;
        long _nodeDeadline;
        public void SetNodeTimeline(int kind,long deadline)
        {
            _nodeBaseKind=kind;_nodeDeadline=deadline;_hasNodeTimeline=true;
            RefreshNodeGeometry();
        }
        void RefreshNodeGeometry()
        {
            bool ready = _nodeBaseKind==1 || _nodeBaseKind==5;
            if(_nodeBaseKind==2 && _nodeDeadline>0 && Game.Framework.Gameplay.LuaGameplayFacade.IsClockSynchronized()
                && long.TryParse(Game.Framework.Gameplay.LuaGameplayFacade.EstimateNowMs(),out long now))ready=now>=_nodeDeadline;
            _ready.SetActive(ready);_cooldown.SetActive(!ready);
        }
        public void SetPlotTimeline(int kind,int crop,long planted,long ready,bool watered,bool fertilized)
        {
            _baseKind=kind;_cropId=crop;_planted=planted;_deadline=ready;_watered=watered;_fert=fertilized;_hasTimeline=true;
            RefreshTimeline();
        }
        void Update()
        {
            if(Time.unscaledTime<_nextRefresh)return;
            _nextRefresh=Time.unscaledTime+.25f;
            if(_hasTimeline)RefreshTimeline();
            if(_hasNodeTimeline)RefreshNodeGeometry();
        }
        void RefreshTimeline()
        {
            int stage=0, kind=_baseKind;
            if(kind==3)stage=3;
            else if(kind==2 && Game.Framework.Gameplay.LuaGameplayFacade.IsClockSynchronized()
                && long.TryParse(Game.Framework.Gameplay.LuaGameplayFacade.EstimateNowMs(),out long now) && _deadline>_planted && _planted>0)
            {
                decimal progress=((decimal)now-_planted)/((decimal)_deadline-_planted);
                if(now>=_deadline){kind=3;stage=3;}
                else stage=progress<.25m?0:progress<.5m?1:2;
            }
            SetPlotData(kind,_cropId,stage,_watered,_fert);
        }
        public void Initialize(HomeVisualCatalog catalog,int typeId)
        {
            _catalog=catalog;
            _visualRoot=new GameObject("VisualRoot").transform;_visualRoot.SetParent(transform,false);
            if(typeId==0)
            {
                _soil=catalog.Spawn("soil_empty",_visualRoot).GetComponentsInChildren<Renderer>();
                _cropAnchor = CreateAnchor("CropAnchor", new Vector3(0, .06f, 0));
                _lock = catalog.Spawn("wood_lock_sign", CreateAnchor("LockMarkerAnchor", new Vector3(.36f, .06f, .3f)));
                _fertilized = catalog.Spawn("fertilized_marker", CreateAnchor("FertilizedMarkerAnchor", new Vector3(-.36f, .06f, .3f)));
                _fertilized.SetActive(false);
                _locked=true;
            }
            else
            {
                string prefix=new[]{"", "tree", "berry", "ore", "flower"}[typeId];
                _ready=catalog.Spawn(prefix+"_ready",_visualRoot);
                _cooldown=catalog.Spawn(prefix+"_cooldown",_visualRoot);_cooldown.SetActive(false);
            }
            var line=new GameObject("StateOutline");line.transform.SetParent(transform,false);
            _outline=line.AddComponent<LineRenderer>();_outline.useWorldSpace=false;_outline.loop=true;
            _outline.positionCount=4;_outline.widthMultiplier=.035f;_outline.sharedMaterial=catalog.indicatorMaterial;
            float r=typeId==1?1.04f:typeId==3?.8f:.53f;
            _outline.SetPositions(new[]{new Vector3(-r,.09f,-r),new Vector3(-r,.09f,r),new Vector3(r,.09f,r),new Vector3(r,.09f,-r)});
            _outline.enabled=false;
        }

        Transform CreateAnchor(string anchorName, Vector3 position)
        {
            var anchor = new GameObject(anchorName).transform;
            anchor.SetParent(_visualRoot, false);
            anchor.localPosition = position;
            return anchor;
        }

        public void SetPlotData(int baseKind,int cropId,int stage,bool watered,bool fertilized)
        {
            _locked=baseKind==0;_lock.SetActive(_locked);
            _fertilized.SetActive(!_locked && fertilized && (baseKind==2||baseKind==3));
            string key=(baseKind==2||baseKind==3)&&cropId>=1&&cropId<=7 ? "crop_"+cropId+"_stage_"+Mathf.Clamp(stage,0,3) : null;
            if(key!=_cropKey)
            {
                if(_crop!=null){_crop.SetActive(false);if(Application.isPlaying)Destroy(_crop);else DestroyImmediate(_crop);}
                _crop=key!=null?_catalog.Spawn(key,_cropAnchor):null;
                _cropKey=key;
            }
            if(_block==null)_block=new MaterialPropertyBlock();
            _block.SetColor("_BaseColor",watered?new Color(.62f,.7f,.78f):Color.white);
            foreach(var renderer in _soil)renderer.SetPropertyBlock(_block);
        }
        public void SetPlotDisplay(HomePlotDisplayKind kind)
        {
            // Overlay updates must not erase the authoritative crop/watering state.
            if(kind==HomePlotDisplayKind.Locked){_locked=true;_lock.SetActive(true);}
            else if(kind==HomePlotDisplayKind.Empty||kind==HomePlotDisplayKind.Growing||kind==HomePlotDisplayKind.Ripe){_locked=false;_lock.SetActive(false);}
            SetOutline((int)kind==4?Color.cyan:(int)kind==5?Color.gray:(int)kind==6?Color.red:new Color(.6f,.3f,.8f),(int)kind>=4);
        }
        public void SetNodeDisplay(HomeNodeDisplayKind kind)
        {
            if(kind==HomeNodeDisplayKind.Cooling){_ready.SetActive(false);_cooldown.SetActive(true);}
            if(kind==HomeNodeDisplayKind.Ready){_ready.SetActive(true);_cooldown.SetActive(false);}
            SetOutline(kind==HomeNodeDisplayKind.Selected?Color.cyan:kind==HomeNodeDisplayKind.Busy?Color.gray:kind==HomeNodeDisplayKind.Locked?new Color(.75f,.5f,.2f):Color.red,kind!=HomeNodeDisplayKind.Ready&&kind!=HomeNodeDisplayKind.Cooling);
        }
        void SetOutline(Color color,bool visible){_outline.startColor=_outline.endColor=color;_outline.enabled=visible;}
    }
}
