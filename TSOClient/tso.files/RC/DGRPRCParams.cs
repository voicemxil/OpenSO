using FSO.Files.Utils;

namespace FSO.Files.RC
{
    public class DGRPRCParams
    {
        public bool[] Rotations = new bool[] { true, true, true, true };
        public bool DoorFix; //depending on subtile, disable certain rotations to fix door.
        public bool CounterFix; //extrapolate z on sides of counter to the edge of the tile.

        public int StartDGRP;
        public int EndDGRP;
        public bool BlenderTweak;
        public bool Simplify = true;

        // Reconstruction quality controls (FSOR version >= 2).
        public bool DepthConditioning = true;   // edge-aware depth dequantize/denoise + speck removal
        public float DepthFilterStrength = 1f;  // range tightness of the dequantize plane fit
        public float DepthDiscontinuity = 0.06f;// depth-space step (0..1) treated as a silhouette break
        public float Quality = 2f;              // decimation target scale (higher keeps more triangles)
        public bool Fusion = false;             // volumetric multi-view fusion (Phase 2, experimental; off — see DGRP3DFusion)

        public bool InRange(int dgrp)
        {
            return ((StartDGRP == EndDGRP && EndDGRP == 0) || (dgrp >= StartDGRP && dgrp <= EndDGRP));
        }

        public DGRPRCParams() { }
        public DGRPRCParams(IoBuffer io, int version)
        {
            Rotations = new bool[4];
            for (int i = 0; i < 4; i++) Rotations[i] = io.ReadByte() > 0;
            DoorFix = io.ReadByte() > 0;
            CounterFix = io.ReadByte() > 0;
            StartDGRP = io.ReadInt32();
            EndDGRP = io.ReadInt32();
            BlenderTweak = io.ReadByte() > 0;
            Simplify = io.ReadByte() > 0;

            if (version >= 2)
            {
                DepthConditioning = io.ReadByte() > 0;
                DepthFilterStrength = io.ReadFloat();
                DepthDiscontinuity = io.ReadFloat();
                Quality = io.ReadFloat();
                Fusion = io.ReadByte() > 0;
            }
        }

        public void Save(IoWriter io)
        {
            foreach (var rotation in Rotations) io.WriteByte((byte)(rotation ? 1 : 0));
            io.WriteByte((byte)(DoorFix ? 1 : 0));
            io.WriteByte((byte)(CounterFix ? 1 : 0));
            io.WriteInt32(StartDGRP);
            io.WriteInt32(EndDGRP);
            io.WriteByte((byte)(BlenderTweak ? 1 : 0));
            io.WriteByte((byte)(Simplify ? 1 : 0));

            // FSOR version >= 2 fields.
            io.WriteByte((byte)(DepthConditioning ? 1 : 0));
            io.WriteFloat(DepthFilterStrength);
            io.WriteFloat(DepthDiscontinuity);
            io.WriteFloat(Quality);
            io.WriteByte((byte)(Fusion ? 1 : 0));
        }
    }
}
