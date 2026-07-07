namespace FSO.TAALab
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            using (var game = new LabGame()) game.Run();
        }
    }
}
