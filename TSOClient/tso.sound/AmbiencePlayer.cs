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

        public AmbiencePlayer(Ambience amb, float volume = 1f)
        {
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

        public void SetVolume(float volume)
        {
            if (fscMode)
            {
                fsc.SetVolume(volume * 0.33f);
            }
            else
            {
                inst.Volume = volume * HITVM.Get().GetMasterVolume(HITVolumeGroup.AMBIENCE);
            }
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
