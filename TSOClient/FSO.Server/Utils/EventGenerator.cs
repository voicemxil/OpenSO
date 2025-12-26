using FSO.Common;
using FSO.Server.Database.DA;
using FSO.Server.Database.DA.Tuning;

namespace FSO.Server.Utils
{
    internal static class EventGenerator
    {
        public static void GenerateEvents(IDAFactory daFactory, EventConfig config)
        {
            using var da = daFactory.Get();

            var presets = da.Tuning.GetAllPresets().ToList();
            var events = da.Events.All(limit: 9999);

            foreach (var modifier in config.modifiers)
            {
                var (start, end) = GetNextRange(modifier.startDate, modifier.endDate);
                foreach (var option in modifier.options)
                {
                    var optionStart = start;
                    var optionEnd = end;

                    if (option.startDate != null && option.endDate != null)
                    {
                        (optionStart, optionEnd) = GetNextRange(option.startDate, option.endDate);
                    }

                    if (!config.timed)
                    {
                        optionStart = DateTime.MinValue;
                        optionEnd = DateTime.MaxValue;
                    }

                    var disabled = !(config.timed ? option.enableTimed : option.enableManual);

                    if (option.tuning.Count > 0)
                    {
                        // Put this option's tuning into a preset
                        var presetLabel = $"{modifier.label}: {option.label}";
                        var presetIdentifier = $"{modifier.name}-{option.name}";

                        var matchingPreset = presets.Find(preset => preset.description == presetIdentifier && preset.flags == 1);

                        if (matchingPreset != null)
                        {
                            EnsurePresetItems(da, matchingPreset, option.tuning);
                        }
                        else
                        {
                            matchingPreset = new Database.DA.Tuning.DbTuningPreset()
                            {
                                name = presetLabel,
                                description = presetIdentifier,
                                flags = 1,
                            };

                            matchingPreset.preset_id = da.Tuning.CreatePreset(matchingPreset);

                            EnsurePresetItems(da, matchingPreset, option.tuning, true);
                        }

                        // Does the event need updated?
                        var existingEvent = events.Find(x => x.type == Database.DA.DbEvents.DbEventType.obj_tuning && x.value == matchingPreset.preset_id);

                        if (existingEvent != null)
                        {
                            // Check the parameters...
                            if (disabled || existingEvent.start_day != optionStart || existingEvent.end_day != optionEnd)
                            {
                                da.Events.Delete(existingEvent.event_id);
                                existingEvent = null;
                            }
                        }

                        if (existingEvent == null && !disabled)
                        {
                            // Create it new
                            da.Events.Add(new Database.DA.DbEvents.DbEvent()
                            {
                                type = Database.DA.DbEvents.DbEventType.obj_tuning,
                                value = matchingPreset.preset_id,
                                value2 = 0,
                                start_day = optionStart,
                                end_day = optionEnd,
                            });
                        }
                    }

                    if (option.gift != null)
                    {
                        var gift = option.gift.Value;
                        int index = 0;
                        foreach (var obj in gift.guids)
                        {
                            string mail_sender = index == 0 ? $"Event: {option.label}" : null;
                            string mail_subject = index == 0 ? gift.title : null;
                            string mail_message = index == 0 ? gift.description : null;

                            index++;

                            // Does the event need updated?
                            var existingEvent = events.Find(x => 
                                x.type == Database.DA.DbEvents.DbEventType.free_object &&
                                x.value == (int)obj &&
                                x.value2 == 1 &&
                                x.mail_sender_name == mail_sender &&
                                x.mail_subject == mail_subject &&
                                x.mail_message == mail_message);

                            if (existingEvent != null)
                            {
                                // Check the parameters...
                                if (disabled || existingEvent.start_day != optionStart || existingEvent.end_day != optionEnd)
                                {
                                    da.Events.Delete(existingEvent.event_id);
                                    existingEvent = null;
                                }
                            }

                            if (existingEvent == null && !disabled)
                            {
                                // Create it new
                                da.Events.Add(new Database.DA.DbEvents.DbEvent()
                                {
                                    type = Database.DA.DbEvents.DbEventType.free_object,
                                    value = (int)obj,
                                    value2 = 1,
                                    mail_sender_name = mail_sender,
                                    mail_subject = mail_subject,
                                    mail_message = mail_message,
                                    start_day = optionStart,
                                    end_day = optionEnd,
                                });
                            }
                        }
                    }
                }
            }
        }

        private static void EnsurePresetItems(IDA da, DbTuningPreset preset, Dictionary<string, float> tuning, bool isNew = false)
        {
            var existing = isNew ? [] : da.Tuning.GetPresetItems(preset.preset_id).ToList();

            foreach (var item in tuning)
            {
                var split = item.Key.Split(':');

                if (split.Length != 3 || !int.TryParse(split[1], out int table) || !int.TryParse(split[2], out int index))
                {
                    continue;
                }

                string type = split[0];

                var existingIndex = existing.FindIndex(x => x.tuning_type == type && x.tuning_table == table && x.tuning_index == index);

                if (existingIndex == -1)
                {
                    da.Tuning.CreatePresetItem(new DbTuningPresetItem()
                    {
                        preset_id = preset.preset_id,
                        tuning_type = type,
                        tuning_table = table,
                        tuning_index = index,
                        value = item.Value,
                    });
                }
                else
                {
                    var existingItem = existing[existingIndex];
                    existing.RemoveAt(existingIndex);

                    if (existingItem.value != item.Value)
                    {
                        da.Tuning.UpdatePresetItemValue(existingItem.item_id, item.Value);
                    }
                }
            }

            // Delete anything that shouldn't be in the preset.
            foreach (var item in existing)
            {
                da.Tuning.DeletePreset(item.item_id);
            }
        }

        private static (DateTime, DateTime) GetNextRange(string start, string end)
        {

            var startDate = GetNextDayMonth(start);
            var endDate = GetNextDayMonth(end);

            var now = DateTime.UtcNow;

            if (startDate > endDate)
            {
                // This implies the event carries through the end of the year into next year.
                if (now > endDate)
                {
                    // Start date is this year, end date is next
                    endDate = endDate.AddYears(1);
                }
                else
                {
                    // Start date was last year (event is currently active)
                    startDate = startDate.AddYears(-1);
                }
            }
            else if (now > endDate)
            {
                // If we're after the end date, move it to next year.
                startDate = startDate.AddYears(1);
                endDate = endDate.AddYears(1);
            }

            return (startDate, endDate);
        }

        private static DateTime GetNextDayMonth(string dayMonth)
        {
            var split = dayMonth.Split('-');

            if (split.Length != 2 || !int.TryParse(split[0], out int day) || !int.TryParse(split[1], out int month))
            {
                throw new InvalidDataException("Event date not correctly formatted, should be day-month.");
            }

            var now = DateTime.UtcNow;

            return new DateTime(now.Year, month, day);
        }
    }
}
