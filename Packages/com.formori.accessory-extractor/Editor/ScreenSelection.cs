using System.Collections.Generic;
using UnityEngine;

namespace AccessoryPrefabMaker
{
    // Face-center selection, like Blender's face selection mode. Z <= 0 is behind the camera.
    public static class ScreenSelection
    {
        public static int[] Box(Vector3[] centers, int[] slots, Rect rect, int slot)
        {
            var result = new List<int>();
            for (int i=0; i<centers.Length; i++)
                if (centers[i].z>0 && (slot<0 || slots[i]==slot) && rect.Contains(centers[i])) result.Add(i);
            return result.ToArray();
        }
        public static int[] Brush(Vector3[] centers, int[] slots, Vector2 from, Vector2 to, float radius, int slot)
        {
            var result = new List<int>();
            var direction=to-from;
            for(int i=0;i<centers.Length;i++)
            {
                if(centers[i].z<=0 || slot>=0 && slots[i]!=slot) continue;
                Vector2 p=centers[i];
                float t=direction.sqrMagnitude>0 ? Mathf.Clamp01(Vector2.Dot(p-from,direction)/direction.sqrMagnitude) : 0;
                if((p-(from+direction*t)).sqrMagnitude<=radius*radius) result.Add(i);
            }
            return result.ToArray();
        }
    }
}
