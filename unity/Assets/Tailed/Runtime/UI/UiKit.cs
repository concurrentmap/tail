using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Tailed.UI
{
    /// <summary>Code-built uGUI helpers with a chunky, high-contrast friendslop style.</summary>
    public static class UiKit
    {
        public static readonly Color Ink = new Color(0.1f, 0.11f, 0.16f, 0.92f);
        public static readonly Color Paper = new Color(0.98f, 0.96f, 0.9f, 1f);
        public static readonly Color Accent = new Color(1f, 0.78f, 0.2f, 1f);
        public static readonly Color MarkRed = new Color(0.95f, 0.3f, 0.3f, 1f);
        public static readonly Color TailBlue = new Color(0.35f, 0.65f, 1f, 1f);
        public static readonly Color Good = new Color(0.4f, 0.85f, 0.45f, 1f);
        public static readonly Color Button = new Color(0.22f, 0.24f, 0.33f, 1f);

        static Font _font;
        public static Font Font => _font ? _font : _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        public static Canvas Canvas(string name, int order)
        {
            var go = new GameObject(name);
            var c = go.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = order;
            var s = go.AddComponent<CanvasScaler>();
            s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            s.referenceResolution = new Vector2(1920, 1080);
            s.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<InputSystemUIInputModule>();
            }
            return c;
        }

        public static RectTransform Rect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        public static RectTransform Stretch(Transform parent, string name, float margin = 0f)
        {
            var rt = Rect(parent, name, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            rt.offsetMin = new Vector2(margin, margin);
            rt.offsetMax = new Vector2(-margin, -margin);
            return rt;
        }

        public static Image Panel(Transform parent, string name, Color color, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var rt = Rect(parent, name, anchor, anchor, anchor, pos, size);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            var o = rt.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(0, 0, 0, 0.6f);
            o.effectDistance = new Vector2(3, -3);
            return img;
        }

        public static Text Label(Transform parent, string text, int size, Color color, TextAnchor align = TextAnchor.UpperLeft, FontStyle style = FontStyle.Bold)
        {
            var rt = Stretch(parent, "Label");
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.fontStyle = style;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            var sh = rt.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0, 0, 0, 0.5f);
            sh.effectDistance = new Vector2(2, -2);
            return t;
        }

        public static Text LabelAt(Transform parent, string text, int size, Color color, Vector2 anchor, Vector2 pos, Vector2 box, TextAnchor align = TextAnchor.UpperLeft)
        {
            var rt = Rect(parent, "Text", anchor, anchor, anchor, pos, box);
            var t = Label(rt, text, size, color, align);
            return t;
        }

        public static Button ButtonAt(Transform parent, string text, Vector2 anchor, Vector2 pos, Vector2 size, Action onClick, Color? color = null, int fontSize = 30)
        {
            var img = Panel(parent, "Button " + text, color ?? Button, anchor, pos, size);
            var b = img.gameObject.AddComponent<UnityEngine.UI.Button>();
            var cb = b.colors;
            cb.highlightedColor = new Color(1.2f, 1.2f, 1.2f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f);
            b.colors = cb;
            b.onClick.AddListener(() => onClick());
            Label(img.transform, text, fontSize, Color.white, TextAnchor.MiddleCenter);
            return b;
        }

        public static InputField Input(Transform parent, string placeholder, Vector2 anchor, Vector2 pos, Vector2 size, int fontSize = 30, bool multiline = false)
        {
            var img = Panel(parent, "Input", Paper, anchor, pos, size);
            var field = img.gameObject.AddComponent<InputField>();
            var text = Label(img.transform, "", fontSize, Color.black, multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft, FontStyle.Normal);
            ((RectTransform)text.transform).offsetMin = new Vector2(12, 6);
            ((RectTransform)text.transform).offsetMax = new Vector2(-12, -6);
            text.supportRichText = false;
            Destroy(text.GetComponent<Shadow>());
            var ph = Label(img.transform, placeholder, fontSize, new Color(0, 0, 0, 0.35f), multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft, FontStyle.Italic);
            ((RectTransform)ph.transform).offsetMin = new Vector2(12, 6);
            ((RectTransform)ph.transform).offsetMax = new Vector2(-12, -6);
            Destroy(ph.GetComponent<Shadow>());
            field.textComponent = text;
            field.placeholder = ph;
            field.lineType = multiline ? InputField.LineType.MultiLineNewline : InputField.LineType.SingleLine;
            return field;
        }

        public static void Destroy(UnityEngine.Object o) => UnityEngine.Object.Destroy(o);

        public static string Clock(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            return $"{(int)seconds / 60}:{(int)seconds % 60:00}";
        }

        public static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }
    }
}
