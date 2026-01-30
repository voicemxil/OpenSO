using FSO.Common.Model;
using FSO.Common.Serialization;
using Microsoft.Xna.Framework;
using Mina.Core.Buffer;

namespace FSO.Server.Protocol.Electron.Packets
{
    public struct SurroundPuppetLotTick
    {
        public uint TickID;
        public SurroundPuppet[] Puppets;

        // Runtime only
        // If this is true, the tick is outdated and shouldn't be sent to anyone who doesn't need an initial state.
        public bool Outdated;
    }

    public struct SurroundPuppetLot
    {
        public uint LotLocation;
        public SurroundPuppetLotTick[] Ticks;
    }

    public class FSOVMSurroundPuppets : AbstractElectronPacket
    {
        private const int MAX_LOTS = 9;
        private const int MAX_TICKS = 64;
        private const int MAX_CHARACTERS = 1024;
        private const int MAX_ANIMATIONS = 10;
        private const int MAX_APPEARANCES = 512;

        public SurroundPuppetLot[] Lots;

        // Runtime only
        public bool NewPlayer;

        public override void Deserialize(IoBuffer input, ISerializationContext context)
        {
            int lotCount = input.GetInt32();
            if (lotCount > MAX_LOTS)
            {
                throw new Exception($"Invalid puppet lot count {lotCount}");
            }

            Lots = new SurroundPuppetLot[lotCount];

            for (int i = 0; i < Lots.Length; i++)
            {
                Lots[i] = new SurroundPuppetLot()
                {
                    LotLocation = input.GetUInt32()
                };

                int tickCount = input.GetInt32();

                if (tickCount > MAX_TICKS)
                {
                    throw new Exception($"Invalid tick count {tickCount}");
                }

                var ticks = new SurroundPuppetLotTick[tickCount];

                for (int j = 0; j < tickCount; j++)
                {
                    var tick = new SurroundPuppetLotTick()
                    {
                        TickID = input.GetUInt32()
                    };

                    int puppetCount = input.GetInt32();

                    if (puppetCount > MAX_CHARACTERS)
                    {
                        throw new Exception($"Invalid character count {tickCount}");
                    }

                    var puppets = new SurroundPuppet[puppetCount];

                    for (int k = 0; k < puppetCount; k++)
                    {
                        puppets[k] = ReadPuppet(input);
                    }

                    tick.Puppets = puppets;

                    ticks[j] = tick;
                }

                Lots[i].Ticks = ticks;
            }
        }

        private static SurroundPuppet ReadPuppet(IoBuffer input)
        {
            var delta = (SurroundPuppetDelta)input.GetInt32();

            SurroundPuppet puppet = new SurroundPuppet()
            {
                Delta = delta
            };

            puppet.PersistID = input.GetUInt32();
            if (delta.HasFlag(SurroundPuppetDelta.BodyInfo))
            {
                puppet.SkinTone = input.GetUInt32();
                puppet.HeadOutfit = input.GetUInt64();
                puppet.BodyOutfit = input.GetUInt64();
                puppet.SkeletonName = input.GetPascalVLCString();
            }
            
            if (delta.HasFlag(SurroundPuppetDelta.Position))
            {
                puppet.VisualPositionStart = new Microsoft.Xna.Framework.Vector4(input.GetSingle(), input.GetSingle(), input.GetSingle(), input.GetSingle());
                puppet.Velocity = new Microsoft.Xna.Framework.Vector4(input.GetSingle(), input.GetSingle(), input.GetSingle(), input.GetSingle());
            }

            if ((delta & SurroundPuppetDelta.Animation) != 0)
            {
                int animationCount = input.GetInt32();
                
                if (animationCount > MAX_ANIMATIONS)
                {
                    throw new Exception($"Invalid animation count {animationCount}");
                }

                var animations = new SurroundPuppetAnimation[animationCount];
                var readName = delta.HasFlag(SurroundPuppetDelta.AnimationNames);
                var readMeta = delta.HasFlag(SurroundPuppetDelta.AnimationState);

                for (int i = 0; i < animations.Length; i++)
                {
                    animations[i] = new SurroundPuppetAnimation(
                        readName ? input.GetPascalVLCString() : null,
                        readMeta ? input.GetSingle() : 0,
                        readMeta ? input.GetSingle() : 0,
                        readMeta ? input.GetSingle() : 0,
                        readMeta ? (SurroundPuppetAnimationFlags)input.GetInt32() : 0);
                }

                puppet.Animations = animations;
            }

            if (delta.HasFlag(SurroundPuppetDelta.Appearances))
            {
                int appearanceCount = input.GetInt32();

                if (appearanceCount > MAX_APPEARANCES)
                {
                    throw new Exception($"Invalid appearance count {appearanceCount}");
                }

                var appearances = new string[appearanceCount];

                for (int i = 0; i < appearances.Length; i++)
                {
                    appearances[i] = input.GetPascalVLCString();
                }

                puppet.Appearances = appearances;
            }

            return puppet;
        }

        public override ElectronPacketType GetPacketType()
        {
            return ElectronPacketType.FSOVMSurroundPuppets;
        }

        public override void Serialize(IoBuffer output, ISerializationContext context)
        {
            output.PutInt32(Lots.Length);

            foreach (ref var lot in Lots.AsSpan())
            {
                output.PutUInt32(lot.LotLocation);

                var tickCount = NewPlayer ? lot.Ticks.Length : lot.Ticks.Count(x => !x.Outdated);

                output.PutInt32(tickCount);

                foreach (ref var tick in lot.Ticks.AsSpan())
                {
                    if (!NewPlayer && tick.Outdated)
                    {
                        continue;
                    }

                    output.PutUInt32(tick.TickID);
                    output.PutInt32(tick.Puppets.Length);

                    foreach (ref var puppet in tick.Puppets.AsSpan())
                    {
                        WritePuppet(output, ref puppet);
                    }
                }
            }
        }

        private static void PutVector4(IoBuffer output, Vector4 vec)
        {
            output.PutSingle(vec.X);
            output.PutSingle(vec.Y);
            output.PutSingle(vec.Z);
            output.PutSingle(vec.W);
        }

        private void WritePuppet(IoBuffer output, ref SurroundPuppet puppet)
        {
            var delta = NewPlayer ? SurroundPuppetDelta.All : puppet.Delta;
            output.PutInt32((int)delta);

            output.PutUInt32(puppet.PersistID);
            if (delta.HasFlag(SurroundPuppetDelta.BodyInfo))
            {
                output.PutUInt32(puppet.SkinTone);
                output.PutUInt64(puppet.HeadOutfit);
                output.PutUInt64(puppet.BodyOutfit);
                output.PutPascalVLCString(puppet.SkeletonName);
            }

            if (delta.HasFlag(SurroundPuppetDelta.Position))
            {
                PutVector4(output, puppet.VisualPositionStart);
                PutVector4(output, puppet.Velocity);
            }

            if ((delta & SurroundPuppetDelta.Animation) != 0)
            {
                output.PutInt32(puppet.Animations.Length);

                foreach (ref var animation in puppet.Animations.AsSpan())
                {
                    if (delta.HasFlag(SurroundPuppetDelta.AnimationNames))
                    {
                        output.PutPascalVLCString(animation.Name);
                    }

                    if (delta.HasFlag(SurroundPuppetDelta.AnimationState))
                    {
                        output.PutSingle(animation.CurrentFrame);
                        output.PutSingle(animation.Speed);
                        output.PutSingle(animation.Weight);
                        output.PutInt32((int)animation.Flags);
                    }
                }
            }

            if (delta.HasFlag(SurroundPuppetDelta.Appearances))
            {
                output.PutInt32(puppet.Appearances.Length);

                foreach (ref var appearance in puppet.Appearances.AsSpan())
                {
                    output.PutPascalVLCString(appearance);
                }
            }
        }
    }
}
