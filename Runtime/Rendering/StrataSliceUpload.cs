using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Strata
{
    // Copies CPU data into one slice of a texture array (a streamed tile's slot) through a staging texture kept per size
    // and format, rather than creating and destroying one for every upload.
    public static class StrataSliceUpload
    {
        private static readonly Dictionary<(int, GraphicsFormat), Texture2D> s_staging = new();

        public static void Copy<T>(NativeArray<T> values, int size, GraphicsFormat format, Texture target, int slice) where T : struct
        {
            if (!s_staging.TryGetValue((size, format), out Texture2D staging) || staging == null)
            {
                staging = new Texture2D(size, size, format, TextureCreationFlags.DontInitializePixels) { hideFlags = HideFlags.HideAndDontSave };
                s_staging[(size, format)] = staging;
            }
            staging.SetPixelData(values, 0);
            staging.Apply(false, false);
            Graphics.CopyTexture(staging, 0, 0, target, slice, 0);
        }
    }
}
