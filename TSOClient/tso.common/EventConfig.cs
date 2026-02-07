namespace FSO.Common
{
    public struct EventCatalogEntry
    {
        public string label;
        public int value;
        public string startDate;
        public string endDate;
    }

    public struct EventModifierGift
    {
        public string title;
        public string description;
        public uint[] guids;
    }

    public struct EventModifierOption
    {
        public string name;
        public string label;
        public string category;
        public string unique;
        public Dictionary<string, float> tuning;
        public bool enableTimed;

        // optional
        public EventModifierGift? gift;
        public string startDate;
        public string endDate;
        public bool enableManual;
    }

    public struct EventModifier
    {
        public string name;
        public string label;
        public string type;
        public string startDate;
        public string endDate;
        public EventModifierOption[] options;
    }

    public struct EventConfig
    {
        public bool timed;
        public EventCatalogEntry[] catalog;
        public EventModifier[] modifiers;

        public static EventConfig FromJson(string json)
        {
            return Newtonsoft.Json.JsonConvert.DeserializeObject<EventConfig>(json);
        }

        public string ToJson()
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(this);
        }
    }
}
