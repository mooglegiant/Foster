﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Foster.Framework;

namespace Foster.GuiSystem
{
    public class Imgui
    {

        #region Structs

        public struct ID
        {
            public readonly int Parent;
            public readonly int Value;
            public readonly int Identifier;

            public ID(int id, ID parent) : this(id, parent.Identifier) { }
            public ID(int id, int parent)
            {
                Parent = parent;
                Value = id;
                Identifier = (Parent + Value).GetHashCode();
            }

            public override bool Equals(object? obj) => obj != null && (obj is ID id) && (this == id);
            public override int GetHashCode() => Identifier;
            public override string ToString() => Identifier.ToString();

            public static bool operator ==(ID a, ID b) => a.Identifier == b.Identifier;
            public static bool operator !=(ID a, ID b) => a.Identifier != b.Identifier;

            public static readonly ID None = new ID(0, 0);
        }

        public struct UniqueInfo
        {
            public int Value;
            public UniqueInfo(int value) => Value = value;

            public static implicit operator UniqueInfo(int id) => new UniqueInfo(id);
            public static implicit operator UniqueInfo(float id) => new UniqueInfo(id.GetHashCode());
            public static implicit operator UniqueInfo(string text) => new UniqueInfo(text.GetHashCode());
        }

        public struct ViewportState
        {
            public ID ID;
            public Batch2d Batcher;
            public Vector2 Scale;
            public Rect Bounds;
            public Vector2 Mouse;
            public Vector2 MouseDelta;
            public bool MouseObstructed;
            public ID LastHotFrame;
            public ID NextHotFrame;
            public int Groups;
        }

        public struct FrameState
        {
            public ID ID;
            public Rect Bounds;
            public Rect Clip;
            public Vector2 Scroll;

            public bool Scrollable;
            public bool Overflow;
            public Vector2 Padding;

            public int Columns;
            public int Column;
            public float ColumnOffset;

            public int Row;
            public float RowHeight;
            public float RowOffset;

            public float InnerWidth => Bounds.Width - Padding.X * 2f;
            public float InnerHeight => RowOffset + RowHeight + Padding.Y * 2f;

            public void NextRow(int columns, float indent, float spacing)
            {
                if (Row > 0)
                    RowOffset += RowHeight + spacing;

                RowHeight = 0;
                Row++;
                Column = 0;
                ColumnOffset = indent;
                Columns = columns;
            }

            public Rect NextCell(float width, float height, float indent, float spacing)
            {
                if (Column >= Columns)
                    NextRow(1, indent, spacing);

                if (Column != 0)
                    ColumnOffset += spacing;

                // determine cell width
                float cellWidth;
                {
                    if (width < 0)
                    {
                        // fill to the right (negative width)
                        cellWidth = InnerWidth - ColumnOffset + width;
                    }
                    else if (width > 0)
                    {
                        // explicit width
                        cellWidth = width;
                    }
                    else
                    {
                        // fill based on our remaining space, divided by remaining elements
                        var remaining = (Columns - Column);
                        cellWidth = (InnerWidth - (remaining - 1) * spacing - ColumnOffset) / remaining;
                    }

                    // can't overflow, clamp cell width
                    if (!Overflow)
                    {
                        var max = InnerWidth - ColumnOffset;
                        cellWidth = Math.Min(max, cellWidth);
                    }

                    // smaller than zero width
                    if (cellWidth < 0)
                        cellWidth = 0;
                }

                // determine cell height
                float cellHeight;
                if (height < 0)
                    cellHeight = Bounds.Height - RowOffset - Padding.Y * 2 + height;
                else if (height > 0)
                    cellHeight = height;
                else
                    cellHeight = Bounds.Height - RowOffset - Padding.Y * 2;

                // position
                var position = new Rect(Bounds.X + Padding.X + ColumnOffset - Scroll.X, Bounds.Y + Padding.Y + RowOffset - Scroll.Y, cellWidth, cellHeight);

                // setup for next cell
                ColumnOffset += cellWidth;
                RowHeight = Math.Max(RowHeight, cellHeight);
                Column++;

                return position;
            }
        }

        public struct Stylesheet
        {
            public SpriteFont Font;
            public float FontSize;
            public float FontScale => FontSize / Font.Height;

            public Vector2 WindowPadding;
            public Color WindowBorderColor;
            public float WindowBorderWeight;
            public Color WindowBackgroundColor;

            public Vector2 FramePadding;
            public Color FrameBorderColor;
            public float FrameBorderWeight;
            public Color FrameBackgroundColor;

            public float ScrollbarWeight;
            public Color ScrollbarColor;
            public Color ScrollbarHotColor;
            public Color ScrollbarActiveColor;

            public Color ItemTextColor;
            public Color ItemTextHotColor;
            public Color ItemTextActiveColor;
            public Color ItemBackgroundColor;
            public Color ItemBackgroundHotColor;
            public Color ItemBackgroundActiveColor;
            public Color ItemBorderColor;
            public Color ItemBorderHotColor;
            public Color ItemBorderActiveColor;
            public float ItemBorderWeight;
            public float ItemSpacing;
            public Vector2 ItemPadding;
            public float ItemHeight => FontSize + ItemPadding.Y * 2;

            public float TitleScale;
        }

        private class Storage<T>
        {
            private Dictionary<ID, T> lastData = new Dictionary<ID, T>();
            private Dictionary<ID, T> nextData = new Dictionary<ID, T>();

            public void Store(ID id, T value)
            {
                nextData[id] = value;
            }

            public bool Retrieve(ID id, out T value)
            {
                return lastData.TryGetValue(id, out value);
            }

            public void Step()
            {
                lastData.Clear();
                foreach (var kv in nextData)
                    lastData[kv.Key] = kv.Value;
                nextData.Clear();
            }
        }

        #endregion

        #region Public Variables

        public Stylesheet DefaultStyle;
        public Stylesheet Style => (styles.Count > 0 ? styles.Peek() : DefaultStyle);
        public float Indent => (indents.Count > 0 ? indents.Peek() : 0f);

        public ID HotId = ID.None;
        public ID LastHotId = ID.None;
        public ID ActiveId = ID.None;
        public ID LastActiveId = ID.None;

        private ID currentId;
        public ID CurrentId
        {
            get => currentId;
            set
            {
                currentId = value;
                if (LastActiveId == currentId)
                    lastActiveIdExists = true;
            }
        }

        private ViewportState viewport;
        private FrameState frame;
        private bool lastActiveIdExists;

        public ViewportState Viewport => viewport;
        public FrameState Frame => frame;
        public Rect Clip => clips.Count > 0 ? clips.Peek() : new Rect();
        public Batch2d Batcher => viewport.Batcher;

        public static float PreferredSize = float.MinValue;

        #endregion

        #region Private Variables

        private readonly Stack<FrameState> frames = new Stack<FrameState>();
        private readonly Stack<ID> ids = new Stack<ID>();
        private readonly Stack<Rect> clips = new Stack<Rect>();
        private readonly Stack<Stylesheet> styles = new Stack<Stylesheet>();
        private readonly Stack<float> indents = new Stack<float>();

        private readonly Storage<ViewportState> viewportStorage = new Storage<ViewportState>();
        private readonly Storage<FrameState> frameStorage = new Storage<FrameState>();
        private readonly Storage<float> floatStorage = new Storage<float>();
        private readonly Storage<bool> boolStorage = new Storage<bool>();

        #endregion

        #region Constructor

        public Imgui(SpriteFont font)
        {
            DefaultStyle = new Stylesheet()
            {
                Font = font,
                FontSize = 16,

                WindowPadding = new Vector2(1, 1),
                WindowBorderColor = 0x5c6063,
                WindowBorderWeight = 1f,
                WindowBackgroundColor = 0x171c20,

                FramePadding = new Vector2(4, 4),
                FrameBorderColor = 0x45494f,
                FrameBorderWeight = 0f,
                FrameBackgroundColor = 0x45494f,

                ScrollbarWeight = 8f,
                ScrollbarColor = 0x639ec0,
                ScrollbarHotColor = 0xa8e5f6,
                ScrollbarActiveColor = 0xee9ec0,

                ItemTextColor = 0xc3c5cf,
                ItemTextHotColor = 0xffffff,
                ItemTextActiveColor = 0x000000,
                ItemBackgroundColor = 0x374953,
                ItemBackgroundHotColor = 0x374953,
                ItemBackgroundActiveColor = 0xee9ec0,
                ItemBorderColor = 0x639ec0,
                ItemBorderHotColor = 0xffffff,
                ItemBorderActiveColor = 0xee9ec0,
                ItemBorderWeight = 1f,
                ItemSpacing = 4,
                ItemPadding = new Vector2(6, 2),

                TitleScale = 1.25f,
            };
        }

        #endregion

        #region State Changing

        public ID Id(UniqueInfo value)
        {
            CurrentId = new ID(value.Value, (ids.Count > 0 ? ids.Peek() : new ID(0, 0)));

            return CurrentId;
        }

        public ID PushId(ID id)
        {
            ids.Push(id);
            return id;
        }

        public ID PushId(UniqueInfo info)
        {
            return PushId(Id(info));
        }

        public void PopId()
        {
            ids.Pop();
        }

        public void Store(ID id, UniqueInfo key, float value)
        {
            floatStorage.Store(new ID(key.Value, id), value);
        }

        public void Store(ID id, UniqueInfo key, bool value)
        {
            boolStorage.Store(new ID(key.Value, id), value);
        }

        public bool Retreive(ID id, UniqueInfo key, out float value)
        {
            return floatStorage.Retrieve(new ID(key.Value, id), out value);
        }

        public bool Retreive(ID id, UniqueInfo key, out bool value)
        {
            return boolStorage.Retrieve(new ID(key.Value, id), out value);
        }

        public void PushStyle(Stylesheet style)
        {
            styles.Push(style);
        }

        public void PopStyle()
        {
            styles.Pop();
        }

        public void PushIndent(float amount)
        {
            indents.Push(Indent + amount);
        }

        public void PopIndent()
        {
            indents.Pop();
        }

        private void PushClip(Rect rect)
        {
            clips.Push(rect);
            viewport.Batcher.SetScissor(rect.Scale(viewport.Scale).Int());
        }

        private void PopClip()
        {
            clips.Pop();
            if (clips.Count > 0)
                viewport.Batcher.SetScissor(clips.Peek().Scale(viewport.Scale).Int());
            else
                viewport.Batcher.SetScissor(null);
        }

        public void Row()
        {
            if (frame.ID == ID.None)
                throw new Exception("You must begin a Frame before creating a Row");

            frame.NextRow(1, Indent, Style.ItemSpacing);
        }
        public void Row(int columns)
        {
            if (frame.ID == ID.None)
                throw new Exception("You must begin a Frame before creating a Row");

            frame.NextRow(columns, Indent, Style.ItemSpacing);
        }

        public Rect Remainder()
        {
            if (frame.ID == ID.None)
                throw new Exception("You must begin a Frame before creating a Cell");

            return frame.NextCell(0, 0, Indent, Style.ItemSpacing);
        }

        public Rect Cell(float height)
        {
            if (frame.ID == ID.None)
                throw new Exception("You must begin a Frame before creating a Cell");

            return frame.NextCell(0f, height, Indent, Style.ItemSpacing);
        }

        public Rect Cell(float width, float height)
        {
            if (frame.ID == ID.None)
                throw new Exception("You must begin a Frame before creating a Cell");

            return frame.NextCell(width, height, Indent, Style.ItemSpacing);
        }

        public void Separator() => Cell(Style.ItemSpacing);
        public void Separator(float height) => Cell(height);
        public void Separator(float width, float height) => Cell(width, height);

        public void Step()
        {
            viewport = new ViewportState();
            frame = new FrameState();
            indents.Clear();
            styles.Clear();
            ids.Clear();
            frames.Clear();
            clips.Clear();
            viewportStorage.Step();
            frameStorage.Step();
            floatStorage.Step();
            boolStorage.Step();

            LastHotId = HotId;
            HotId = ID.None;

            // track active ID
            // if the active ID no longer exists, unset it
            {
                if (LastActiveId == ID.None && ActiveId != ID.None)
                    lastActiveIdExists = true;

                LastActiveId = ActiveId;

                if (!lastActiveIdExists)
                    ActiveId = ID.None;

                lastActiveIdExists = false;
            }
        }

        #endregion

        #region Viewports / Frames

        public void BeginViewport(Window window, Batch2d batcher, Vector2? contentScale = null)
        {
            var scale = window.ContentScale * (contentScale ?? Vector2.One);
            var bounds = new Rect(0, 0, window.DrawableWidth / scale.X, window.DrawableHeight / scale.Y);
            var mouse = window.DrawableMouse / scale;

            BeginViewport(window.Title, batcher, bounds, mouse, scale, !window.MouseOver);
        }

        public void BeginViewport(UniqueInfo info, Batch2d batcher, Rect bounds, Vector2 mouse, Vector2 scale, bool mouseObstructed = false)
        {
            if (viewport.ID != ID.None)
                throw new Exception("The previous Viewport must be ended before beginning a new one");

            viewport = new ViewportState
            {
                ID = new ID(info.Value, 0),
                Bounds = bounds,
                Mouse = mouse,
                MouseObstructed = mouseObstructed,
                Scale = scale,
                Batcher = batcher
            };

            if (viewportStorage.Retrieve(viewport.ID, out var lastViewport))
            {
                viewport.MouseDelta = mouse - lastViewport.Mouse;
                viewport.LastHotFrame = lastViewport.NextHotFrame;
            }

            PushClip(viewport.Bounds);
            viewport.Batcher.PushMatrix(Matrix3x2.CreateScale(scale));
        }

        public void EndViewport()
        {
            if (viewport.ID == ID.None)
                throw new Exception("You must Begin a Viewport before ending it");
            if (frame.ID != ID.None)
                throw new Exception("The previous Group must be closed before closing the Viewport");

            PopClip();

            viewportStorage.Store(viewport.ID, viewport);
            viewport.Batcher.PopMatrix();
            viewport = new ViewportState();
        }

        public bool BeginFrame(UniqueInfo info, Rect bounds, bool scrollable = true)
        {
            if (viewport.ID == ID.None)
                throw new Exception("You must open a Viewport before beginning a Frame");

            var isWindow = frames.Count <= 0;
            var clip = bounds.OverlapRect(clips.Peek());
            clip = clip.Inflate(-(isWindow ? Style.WindowBorderWeight : Style.FrameBorderWeight));

            if (clip.Area > 0)
            {
                frames.Push(frame);
                frame = new FrameState
                {
                    ID = PushId(info),
                    Bounds = bounds,
                    Clip = clip,
                    Padding = (isWindow ? Style.WindowPadding : Style.FramePadding),
                    Scrollable = scrollable
                };

                if (!frameStorage.Retrieve(frame.ID, out var last))
                    last.ID = ID.None;

                if (frame.Clip.Contains(viewport.Mouse))
                    viewport.NextHotFrame = frame.ID;

                if (isWindow)
                {
                    viewport.Batcher.Rect(bounds, Style.WindowBackgroundColor);
                    viewport.Batcher.HollowRect(bounds, Style.WindowBorderWeight, Style.WindowBorderColor);
                }
                else
                {
                    viewport.Batcher.Rect(bounds, Style.FrameBackgroundColor);
                    viewport.Batcher.HollowRect(bounds, Style.FrameBorderWeight, Style.FrameBorderColor);
                }

                // handle vertical scrolling
                if (frame.Scrollable)
                {
                    frame.Scroll = Vector2.Zero;
                    if (last.ID != ID.None)
                        frame.Scroll = last.Scroll;

                    if (last.InnerHeight > frame.Bounds.Height)
                    {
                        frame.Bounds.Width -= 16;

                        frame.Scroll.Y = Calc.Clamp(frame.Scroll.Y, 0, last.InnerHeight - frame.Bounds.Height);

                        var scrollId = Id("Scroll-Y");
                        var scrollRect = VerticalScrollBar(frame.Bounds, frame.Scroll, last.InnerHeight);
                        var buttonRect = scrollRect.OverlapRect(frame.Clip);
                        var scrollColor = Style.ScrollbarColor;

                        if (buttonRect.Area > 0)
                        {
                            ImguiButton.ButtonBehaviour(this, scrollId, buttonRect);

                            if (ActiveId == scrollId)
                            {
                                var relativeSpeed = (frame.Bounds.Height / scrollRect.Height);
                                frame.Scroll.Y = Calc.Clamp(frame.Scroll.Y + viewport.MouseDelta.Y * relativeSpeed, 0, last.InnerHeight - bounds.Height);
                                scrollRect = VerticalScrollBar(frame.Bounds, frame.Scroll, last.InnerHeight);
                                scrollColor = Style.ScrollbarActiveColor;
                            }
                            else if (HotId == scrollId)
                            {
                                scrollColor = Style.ScrollbarHotColor;
                            }

                            viewport.Batcher.Rect(scrollRect.Inflate(-4), scrollColor);
                        }

                        frame.Clip.Width -= 16;
                    }
                    else
                    {
                        frame.Scroll = Vector2.Zero;
                    }

                    PushClip(frame.Clip);
                }

                // behave as a button
                // this way stuff can check if it's the ActiveID
                ImguiButton.ButtonBehaviour(this, frame.ID, frame.Clip);

                return true;
            }
            else
            {
                return false;
            }
        }

        public bool BeginFrame(UniqueInfo info, float height)
        {
            if (frame.ID != ID.None)
                return BeginFrame(info, Cell(height));
            else
                return BeginFrame(info, viewport.Bounds);
        }

        public void EndFrame()
        {
            if (frame.ID == ID.None)
                throw new Exception("You must Begin a Frame before ending it");

            frameStorage.Store(frame.ID, frame);
            PopId();

            if (frame.Scrollable)
                PopClip();

            if (frames.Count > 0)
                frame = frames.Pop();
            else
                frame = new FrameState();
        }

        private Rect VerticalScrollBar(Rect bounds, Vector2 scroll, float innerHeight)
        {
            var barH = bounds.Height * (bounds.Height / innerHeight);
            var barY = (bounds.Height - barH) * (scroll.Y / (innerHeight - bounds.Height));

            return new Rect(bounds.Right, bounds.Y + barY, 16f, barH);
        }

        #endregion

        #region Utils

        public bool MouseOver(ID id, Rect position)
        {
            if (viewport.MouseObstructed)
                return false;

            if (ActiveId != ID.None && ActiveId != id)
                return false;

            if (frame.ID != ID.None && viewport.LastHotFrame != ID.None && viewport.LastHotFrame != frame.ID)
                return false;

            if (App.Input.Mouse.LeftDown && !App.Input.Mouse.LeftPressed)
                return false;

            if (!Clip.Contains(viewport.Mouse) || !position.Contains(viewport.Mouse))
                return false;

            return true;
        }

        public bool HoveringOrDragging(ID id)
        {
            return LastHotId == id || LastActiveId == id;
        }

        #endregion
    }
}
