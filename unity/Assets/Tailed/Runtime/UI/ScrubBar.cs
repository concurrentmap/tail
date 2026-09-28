using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Tailed.UI
{
    /// <summary>
    /// Replay timeline: a bar with a fill, event ticks and a playhead. Click or drag to seek;
    /// reports a 0..1 fraction.
    /// </summary>
    public sealed class ScrubBar : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        public event Action<float> Seek;
        RectTransform _fill, _head, _ticks;

        public static ScrubBar Create(Transform parent, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var bg = UiKit.Panel(parent, "Scrub", new Color(0.12f, 0.12f, 0.16f, 1f), anchor, pos, size);
            var bar = bg.gameObject.AddComponent<ScrubBar>();
            bar._ticks = UiKit.Stretch(bg.transform, "Ticks");
            bar._fill = UiKit.Rect(bg.transform, "Fill", new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f), Vector2.zero, new Vector2(0, 0));
            var fill = bar._fill.gameObject.AddComponent<Image>();
            fill.color = new Color(1f, 0.8f, 0.2f, 0.35f);
            fill.raycastTarget = false;
            bar._head = UiKit.Rect(bg.transform, "Head", new Vector2(0, 0), new Vector2(0, 1), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4, 8));
            var head = bar._head.gameObject.AddComponent<Image>();
            head.color = Color.white;
            head.raycastTarget = false;
            return bar;
        }

        /// <summary>A coloured tick at a fraction along the bar (events: flags, pins, stops).</summary>
        public void Tick(float fraction, Color color, float height = 0.7f)
        {
            var rt = UiKit.Rect(_ticks, "Tick", new Vector2(fraction, 0.5f - height * 0.5f), new Vector2(fraction, 0.5f + height * 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(3, 0));
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }

        public void SetFraction(float f)
        {
            f = Mathf.Clamp01(f);
            _fill.anchorMax = new Vector2(f, 1);
            _fill.sizeDelta = Vector2.zero;
            _head.anchorMin = new Vector2(f, 0);
            _head.anchorMax = new Vector2(f, 1);
        }

        public void OnPointerDown(PointerEventData e) => Report(e);
        public void OnDrag(PointerEventData e) => Report(e);

        void Report(PointerEventData e)
        {
            var rt = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out var local)) return;
            var r = rt.rect;
            Seek?.Invoke(Mathf.Clamp01((local.x - r.xMin) / r.width));
        }
    }
}
