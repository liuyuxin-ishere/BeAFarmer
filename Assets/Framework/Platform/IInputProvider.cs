using System;
using UnityEngine;

namespace Game.Framework.Platform
{
    /// <summary>
    /// 玩法层读取的输入。坐标是屏幕像素，移动向量已归一化。
    /// </summary>
    public interface IInputProvider
    {
        /// <summary>最近一次 Tick 的移动。没有输入时是零向量，长度不超过 1。</summary>
        Vector2 ReadMove();

        /// <summary>最近一次 Tick 的指针位置，单位是屏幕像素。</summary>
        Vector2 ReadPointer();

        /// <summary>最近一次 Tick 是否把这次按下识别成点击。</summary>
        bool WasTapPressed();

        /// <summary>最近一次 Tick 的滚轮 y。</summary>
        float ReadZoom();

        /// <summary>拖拽开始。参数是按下时的屏幕像素。</summary>
        event Action<Vector2> DragStarted;

        /// <summary>拖拽过程中指针位置变化。参数是当前屏幕像素。</summary>
        event Action<Vector2> Dragged;

        /// <summary>拖拽结束。参数是松开时的屏幕像素。</summary>
        event Action<Vector2> DragEnded;
    }
}
