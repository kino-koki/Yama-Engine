using System.Runtime.InteropServices;

namespace ShanyuEngine.NativeInterop
{
    public static class NativePhysics
    {
        private const string DllName = "ShanyuEngine.Native";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int shanyu_physics_broadphase_sap(
            float[] aabbs, int bodyCount, int[] outPairs, int maxPairs);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern float shanyu_physics_gjk_distance(
            float[] verticesA, int countA, float[] verticesB, int countB,
            float[] transformA, float[] transformB,
            float[] outNormal, float[] outPointA, float[] outPointB);
    }
}
