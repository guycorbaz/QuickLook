﻿// Copyright © 2018 Paddy Xu
// 
// This file is part of QuickLook program.
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using LibAPNG;
using QuickLook.Common.ExtensionMethods;

namespace QuickLook.Plugin.ImageViewer.AnimatedImage
{
    internal class APngAnimationProvider : AnimationProvider
    {
        private readonly Frame _baseFrame;
        private readonly List<FrameInfo> _frames;
        private readonly List<BitmapSource> _renderedFrames;
        private int _lastEffectivePreviousPreviousFrameIndex;
        private NativeImageProvider _nativeImageProvider;

        public APngAnimationProvider(string path, NConvert meta) : base(path, meta)
        {
            if (!IsAnimatedPng(path))
            {
                _nativeImageProvider = new NativeImageProvider(path, meta);
                Animator = _nativeImageProvider.Animator;
                return;
            }

            var decoder = new APNGBitmap(path);

            _baseFrame = decoder.DefaultImage;
            _frames = new List<FrameInfo>(decoder.Frames.Length);
            _renderedFrames = new List<BitmapSource>(decoder.Frames.Length);
            Enumerable.Repeat(0, decoder.Frames.Length).ForEach(_ => _renderedFrames.Add(null));

            Animator = new Int32AnimationUsingKeyFrames {RepeatBehavior = RepeatBehavior.Forever};

            var wallclock = TimeSpan.Zero;

            for (var i = 0; i < decoder.Frames.Length; i++)
            {
                var frame = decoder.Frames[i];

                _frames.Add(new FrameInfo(decoder.IHDRChunk, frame));

                Animator.KeyFrames.Add(new DiscreteInt32KeyFrame(i, KeyTime.FromTimeSpan(wallclock)));
                wallclock += _frames[i].Delay;
            }
        }

        public override Task<BitmapSource> GetThumbnail(Size size, Size fullSize)
        {
            if (_nativeImageProvider != null)
                return _nativeImageProvider.GetThumbnail(size, fullSize);

            return new Task<BitmapSource>(() => _baseFrame.GetBitmapSource());
        }

        public override Task<BitmapSource> GetRenderedFrame(int index)
        {
            if (_nativeImageProvider != null)
                return _nativeImageProvider.GetRenderedFrame(index);

            if (_renderedFrames[index] != null)
                return new Task<BitmapSource>(() => _renderedFrames[index]);

            return new Task<BitmapSource>(() =>
            {
                var rendered = Render(index);
                _renderedFrames[index] = rendered;

                return rendered;
            });
        }

        public override void Dispose()
        {
            if (_nativeImageProvider != null)
            {
                _nativeImageProvider.Dispose();
                _nativeImageProvider = null;
                return;
            }

            _frames.Clear();
            _renderedFrames.Clear();
        }

        private BitmapSource Render(int index)
        {
            var currentFrame = _frames[index];
            FrameInfo previousFrame = null;
            BitmapSource previousRendered = null;
            BitmapSource previousPreviousRendered = null;

            if (index > 0)
            {
                if (_renderedFrames[index - 1] == null)
                    _renderedFrames[index - 1] = Render(index - 1);

                previousFrame = _frames[index - 1];
                previousRendered = _renderedFrames[index - 1];
            }

            // when saying APNGDisposeOpPrevious, we need to find the last frame not having APNGDisposeOpPrevious.
            // Only [index-2] is not correct here since that frame may also have APNGDisposeOpPrevious.
            if (index > 1)
                previousPreviousRendered = _renderedFrames[_lastEffectivePreviousPreviousFrameIndex];
            if (_frames[index].DisposeOp != DisposeOps.APNGDisposeOpPrevious)
                _lastEffectivePreviousPreviousFrameIndex = Math.Max(_lastEffectivePreviousPreviousFrameIndex, index);

            var visual = new DrawingVisual();

            using (var context = visual.RenderOpen())
            {
                // protect region
                if (currentFrame.BlendOp == BlendOps.APNGBlendOpSource)
                {
                    var freeRegion = new CombinedGeometry(GeometryCombineMode.Xor,
                        new RectangleGeometry(currentFrame.FrameRect),
                        new RectangleGeometry(currentFrame.FrameRect));
                    context.PushOpacityMask(
                        new DrawingBrush(new GeometryDrawing(Brushes.Transparent, null, freeRegion)));
                }

                if (previousFrame != null)
                    switch (previousFrame.DisposeOp)
                    {
                        case DisposeOps.APNGDisposeOpNone:
                            if (previousRendered != null)
                                context.DrawImage(previousRendered, currentFrame.FullRect);
                            break;
                        case DisposeOps.APNGDisposeOpPrevious:
                            if (previousPreviousRendered != null)
                                context.DrawImage(previousPreviousRendered, currentFrame.FullRect);
                            break;
                        case DisposeOps.APNGDisposeOpBackground:
                            // do nothing
                            break;
                    }

                // unprotect region and draw current frame
                if (currentFrame.BlendOp == BlendOps.APNGBlendOpSource)
                    context.Pop();
                context.DrawImage(currentFrame.Pixels, currentFrame.FrameRect);
            }

            var bitmap = new RenderTargetBitmap(
                (int) currentFrame.FullRect.Width, (int) currentFrame.FullRect.Height,
                Math.Floor(currentFrame.Pixels.DpiX), Math.Floor(currentFrame.Pixels.DpiY),
                PixelFormats.Pbgra32);
            bitmap.Render(visual);

            bitmap.Freeze();
            return bitmap;
        }

        private static bool IsAnimatedPng(string path)
        {
            using (var br = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            {
                while (br.BaseStream.Length - br.BaseStream.Position >= 4)
                {
                    var window = br.ReadBytes(4);

                    if (window[0] == 'I' && window[1] == 'D' && window[2] == 'A' && window[3] == 'T')
                        return false;

                    if (window[0] == 'a' && window[1] == 'c' && window[2] == 'T' && window[3] == 'L')
                        return true;

                    br.BaseStream.Position -= 3;
                }

                return false;
            }
        }

        private class FrameInfo
        {
            public readonly BlendOps BlendOp;
            public readonly TimeSpan Delay;
            public readonly DisposeOps DisposeOp;
            public readonly Rect FrameRect;
            public readonly Rect FullRect;
            public readonly BitmapSource Pixels;

            public FrameInfo(IHDRChunk header, Frame frame)
            {
                FullRect = new Rect(0, 0, header.Width, header.Height);
                FrameRect = new Rect(frame.fcTLChunk.XOffset, frame.fcTLChunk.YOffset,
                    frame.fcTLChunk.Width, frame.fcTLChunk.Height);

                BlendOp = frame.fcTLChunk.BlendOp;
                DisposeOp = frame.fcTLChunk.DisposeOp;

                Pixels = frame.GetBitmapSource();
                Pixels.Freeze();

                Delay = TimeSpan.FromSeconds((double) frame.fcTLChunk.DelayNum /
                                             (frame.fcTLChunk.DelayDen == 0
                                                 ? 100
                                                 : frame.fcTLChunk.DelayDen));
            }
        }
    }
}