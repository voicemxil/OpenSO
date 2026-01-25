using FSO.Files.XA;
using Microsoft.Xna.Framework.Audio;
using FSO.HIT.Model;

namespace FSO.HIT
{
    public class AmbiencePlayer
    {
        private bool fscMode;
        private FSCPlayer fsc;
        private SoundEffect sfx;
        private SoundEffectInstance inst;

        private float PositionalVolume;
        public float Volume { get; private set; }
        private float TargetVolume;
        private float VolumeChangeSpeed;

        public AmbiencePlayer(Ambience amb, float volume = 1f)
        {
            Volume = volume;
            PositionalVolume = volume;

            if (amb.Loop)
            {
                byte[] data = new XAFile(FSO.Content.Content.Get().GetPath(amb.Path)).DecompressedData;
                var stream = new MemoryStream(data);
                sfx = SoundEffect.FromStream(stream);
                stream.Close();

                inst = sfx.CreateInstance();
                inst.IsLooped = true;
                inst.Volume = volume * HITVM.Get().GetMasterVolume(HITVolumeGroup.AMBIENCE);
                inst.Play();
                HITVM.Get().AmbLoops.Add(inst);

                fscMode = false;
            }
            else
            {
                fsc = HITVM.Get().PlayFSC(FSO.Content.Content.Get().GetPath(amb.Path));
                fsc.SetVolume(volume * 0.33f); //may need tweaking
                fscMode = true;
            }
        }

        private void UpdateVolume()
        {
            if (fscMode)
            {
                fsc.SetVolume(Volume * PositionalVolume * 0.33f);
            }
            else
            {
                inst.Volume = Volume * PositionalVolume * HITVM.Get().GetMasterVolume(HITVolumeGroup.AMBIENCE);
            }
        }

        public void SetPositionalVolume(float volume)
        {
            PositionalVolume = volume;
            UpdateVolume();
        }

        public void SetVolume(float volume)
        {
            Volume = volume;

            UpdateVolume();

            TargetVolume = volume;
            VolumeChangeSpeed = 0;
        }

        public void SetVolume(float volume, float transitionDuration)
        {
            if (transitionDuration == 0)
            {
                SetVolume(volume);
                return;
            }

            TargetVolume = volume;
            VolumeChangeSpeed = (volume - Volume) / transitionDuration;
        }

        public bool TickVolume(float delta)
        {
            if (Volume == TargetVolume)
            {
                return true;
            }

            var below = Volume < TargetVolume;

            Volume += VolumeChangeSpeed * delta;

            var newBelow = Volume < TargetVolume;

            if (below != newBelow)
            {
                Volume = TargetVolume;
                VolumeChangeSpeed = 0;

                return true;
            }

            return false;
        }


        public bool HasTransition()
        {
            return VolumeChangeSpeed != 0;
        }

        public void Pause()
        {
            if (fscMode)
            {
                fsc.Pause();
            }
            else
            {
                inst.Pause();
            }
        }

        public void Resume()
        {
            if (fscMode)
            {
                fsc.Resume();
            }
            else
            {
                inst.Resume();
            }
        }

        public void Kill()
        {
            if (fscMode) HITVM.Get().StopFSC(fsc);
            else
            {
                inst.Stop();
                inst.Dispose();
                HITVM.Get().AmbLoops.Remove(inst);
                sfx.Dispose();
            }
        }
    }

    public struct Ambience
    {
        public string Path;
        public bool Loop; //certain ambiences are simple xa loops instead of fscs.

        public Ambience(string path, bool loop)
        {
            Path = path;
            Loop = loop;
        }
    }
}
