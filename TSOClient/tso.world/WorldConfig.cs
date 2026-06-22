using FSO.Common;
using FSO.LotView.Model;

namespace FSO.LotView
{
    public class WorldConfig
    {
        public static WorldConfig Current = new WorldConfig();

        //(off, advanced, +3d wall, ultra)
        public int LightingMode;

        public bool AdvancedLighting
        {
            get
            {
                return (LightingMode > 0);
            }
        }
        public bool Shadow3D
        {
            get
            {
                return (LightingMode > 1);
            }
        }
        public bool UltraLighting
        {
            get
            {
                return (LightingMode > 2);
            }
        }
        public bool Weather = true;
        public int SurroundingLots = 0;
        public bool SmoothZoom
        {
            get
            {
                return _EnableTransitions;
            }
            set
            {

            }
        }
        public int AA = 0; //legacy AA preset (unused by ChangeAAMode now; kept for compatibility)

        // Decoupled anti-aliasing pipeline (see World.ChangeAAMode).
        public int MSAA = 0;            //hardware MSAA samples: 0/2/4/8
        public int SuperSampling = 1;   //supersample factor: 1 (off) or 2
        public int PostAA = 0;          //0=Off, 1=FXAA, 2=SMAA-Low, 3=SMAA-High (post-process shader pass)
        public int Sharpen = 0;         //0=Bilinear, 1=FSR (EASU+RCAS) resolve
        public float SharpenAmount = 0.25f;

        public bool Directional = true;
        public bool Complex = false;

        private bool _EnableTransitions = false;
        public bool EnableTransitions
        {
            get
            {
                return _EnableTransitions;
            }
            set
            {
                _EnableTransitions = FSOEnvironment.Enable3D && value;
            }
        }

        public GlobalGraphicsMode Mode = GlobalGraphicsMode.Hybrid2D;

        public int PassOffset
        {
            get {
                return (AdvancedLighting)?1:0;
            }
        }

        public int DirPassOffset
        {
            get
            {
                return (AdvancedLighting) ? ((Directional)?1:1) : 0;
            }
        }
    }
}
