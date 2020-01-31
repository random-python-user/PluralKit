using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Humanizer;
using NodaTime;
using NodaTime.Text;
using NodaTime.TimeZones;

using PluralKit.Bot.CommandSystem;
using PluralKit.Core;

namespace PluralKit.Bot.Commands
{
    public class SystemCommands
    {
        private IDataStore _data;
        private EmbedService _embeds;

        private ProxyCacheService _proxyCache;

        public SystemCommands(EmbedService embeds, ProxyCacheService proxyCache, IDataStore data)
        {
            _embeds = embeds;
            _proxyCache = proxyCache;
            _data = data;
        }
        
        public async Task Query(Context ctx, PKSystem system) {
            if (system == null) throw Errors.NoSystemError;

            await ctx.Reply(embed: await _embeds.CreateSystemEmbed(system, ctx.LookupContextFor(system)));
        }
        
        public async Task New(Context ctx)
        {
            ctx.CheckNoSystem();

            var system = await _data.CreateSystem(ctx.RemainderOrNull());
            await _data.AddAccount(system, ctx.Author.Id);
            await ctx.Reply($"{Emojis.Success} Your system has been created. Type `pk;system` to view it, and type `pk;help` for more information about commands you can use now.");
        }
        
        public async Task Name(Context ctx)
        {
            ctx.CheckSystem();

            var newSystemName = ctx.RemainderOrNull();
            if (newSystemName != null && newSystemName.Length > Limits.MaxSystemNameLength) throw Errors.SystemNameTooLongError(newSystemName.Length);

            ctx.System.Name = newSystemName;
            await _data.SaveSystem(ctx.System);
            await ctx.Reply($"{Emojis.Success} System name {(newSystemName != null ? "changed" : "cleared")}.");
        }
        
        public async Task Description(Context ctx) {
            ctx.CheckSystem();

            var newDescription = ctx.RemainderOrNull();
            if (newDescription != null && newDescription.Length > Limits.MaxDescriptionLength) throw Errors.DescriptionTooLongError(newDescription.Length);

            ctx.System.Description = newDescription;
            await _data.SaveSystem(ctx.System);
            await ctx.Reply($"{Emojis.Success} System description {(newDescription != null ? "changed" : "cleared")}.");
        }
        
        public async Task Tag(Context ctx)
        {
            ctx.CheckSystem();

            var newTag = ctx.RemainderOrNull();
            ctx.System.Tag = newTag;

            if (newTag != null)
                if (newTag.Length > Limits.MaxSystemTagLength)
                    throw Errors.SystemNameTooLongError(newTag.Length);

            await _data.SaveSystem(ctx.System);
            await ctx.Reply($"{Emojis.Success} System tag {(newTag != null ? $"changed. Member names will now end with `{newTag.SanitizeMentions()}` when proxied" : "cleared")}.");
            
            await _proxyCache.InvalidateResultsForSystem(ctx.System);
        }
        
        public async Task SystemAvatar(Context ctx)
        {
            ctx.CheckSystem();
            
            if (ctx.RemainderOrNull() == null && ctx.Message.Attachments.Count == 0)
            {
                if ((ctx.System.AvatarUrl?.Trim() ?? "").Length > 0)
                {
                    var eb = new EmbedBuilder()
                        .WithTitle($"System avatar")
                        .WithImageUrl(ctx.System.AvatarUrl)
                        .WithDescription($"To clear, use `pk;system avatar clear`.");
                    await ctx.Reply(embed: eb.Build());
                }
                else
                    throw new PKSyntaxError($"This system does not have an avatar set. Set one by attaching an image to this command, or by passing an image URL or @mention.");

                return;
            }

            var member = await ctx.MatchUser();
            if (member != null)
            {
                if (member.AvatarId == null) throw Errors.UserHasNoAvatar;
                ctx.System.AvatarUrl = member.GetAvatarUrl(ImageFormat.Png, size: 256);
                await _data.SaveSystem(ctx.System);
            
                var embed = new EmbedBuilder().WithImageUrl(ctx.System.AvatarUrl).Build();
                await ctx.Reply(
                    $"{Emojis.Success} System avatar changed to {member.Username}'s avatar! {Emojis.Warn} Please note that if {member.Username} changes their avatar, the system's avatar will need to be re-set.", embed: embed);
            }
            else if (ctx.Match("clear"))
            {
                ctx.System.AvatarUrl = null;
                await _data.SaveSystem(ctx.System);
                await ctx.Reply($"{Emojis.Success} System avatar cleared.");
            }
            else
            {
                // They can't both be null - otherwise we would've hit the conditional at the very top
                string url = ctx.RemainderOrNull() ?? ctx.Message.Attachments.FirstOrDefault()?.ProxyUrl;
                await ctx.BusyIndicator(() => Utils.VerifyAvatarOrThrow(url));

                ctx.System.AvatarUrl = url;
                await _data.SaveSystem(ctx.System);

                var embed = url != null ? new EmbedBuilder().WithImageUrl(url).Build() : null;
                await ctx.Reply($"{Emojis.Success} System avatar changed.", embed: embed);
            }
            
            await _proxyCache.InvalidateResultsForSystem(ctx.System);
        }
        
        public async Task Delete(Context ctx) {
            ctx.CheckSystem();

            var msg = await ctx.Reply($"{Emojis.Warn} Are you sure you want to delete your system? If so, reply to this message with your system's ID (`{ctx.System.Hid}`).\n**Note: this action is permanent.**");
            var reply = await ctx.AwaitMessage(ctx.Channel, ctx.Author, timeout: TimeSpan.FromMinutes(1));
            if (reply.Content != ctx.System.Hid) throw new PKError($"System deletion cancelled. Note that you must reply with your system ID (`{ctx.System.Hid}`) *verbatim*.");

            await _data.DeleteSystem(ctx.System);
            await ctx.Reply($"{Emojis.Success} System deleted.");
            
            await _proxyCache.InvalidateResultsForSystem(ctx.System);
        }
        
        public async Task MemberShortList(Context ctx, PKSystem system) {
            if (system == null) throw Errors.NoSystemError;
            ctx.CheckSystemPrivacy(system, system.MemberListPrivacy);
            
            var authCtx = ctx.LookupContextFor(system);
            var shouldShowPrivate = authCtx == LookupContext.ByOwner && ctx.Match("all", "everyone", "private");

            var embedTitle = system.Name != null ? $"Members of {system.Name.SanitizeMentions()} (`{system.Hid}`)" : $"Members of `{system.Hid}`";

            var memberCountPublic = _data.GetSystemMemberCount(system, false);
            var memberCountAll = _data.GetSystemMemberCount(system, true);
            await Task.WhenAll(memberCountPublic, memberCountAll);

            var memberCountDisplayed = shouldShowPrivate ? memberCountAll.Result : memberCountPublic.Result;

            var members = _data.GetSystemMembers(system)
                .Where(m => m.MemberPrivacy == PrivacyLevel.Public || shouldShowPrivate)
                .OrderBy(m => m.Name, StringComparer.InvariantCultureIgnoreCase);
            var anyMembersHidden = !shouldShowPrivate && memberCountPublic.Result != memberCountAll.Result;
                
            await ctx.Paginate(
                members,
                memberCountDisplayed,
                25,
                embedTitle,
                (eb, ms) =>
                {
                    eb.Description = string.Join("\n", ms.Select((m) =>
                    {
                        if (m.HasProxyTags)
                        {
                            var proxyTagsString = m.ProxyTagsString().SanitizeMentions();
                            if (proxyTagsString.Length > 100) // arbitrary threshold for now, tweak?
                                proxyTagsString = "tags too long, see member card";
                            
                            return $"[`{m.Hid}`] **{m.Name.SanitizeMentions()}** *({proxyTagsString})*";
                        }

                        return $"[`{m.Hid}`] **{m.Name.SanitizeMentions()}**";
                    }));

                    var footer = $"{memberCountDisplayed} total.";
                    if (anyMembersHidden && authCtx == LookupContext.ByOwner)
                        footer += "Private members have been hidden. type \"pk;system list all\" to include them.";
                    eb.WithFooter(footer);
                    
                    return Task.CompletedTask;
                });
        }

        public async Task MemberLongList(Context ctx, PKSystem system) {
            if (system == null) throw Errors.NoSystemError;
            ctx.CheckSystemPrivacy(system, system.MemberListPrivacy);
            
            var authCtx = ctx.LookupContextFor(system);
            var shouldShowPrivate = authCtx == LookupContext.ByOwner && ctx.Match("all", "everyone", "private");
            
            var embedTitle = system.Name != null ? $"Members of {system.Name} (`{system.Hid}`)" : $"Members of `{system.Hid}`";

            var memberCountPublic = _data.GetSystemMemberCount(system, false);
            var memberCountAll = _data.GetSystemMemberCount(system, true);
            await Task.WhenAll(memberCountPublic, memberCountAll);

            var memberCountDisplayed = shouldShowPrivate ? memberCountAll.Result : memberCountPublic.Result;

            var members = _data.GetSystemMembers(system)
                .Where(m => m.MemberPrivacy == PrivacyLevel.Public || shouldShowPrivate)
                .OrderBy(m => m.Name, StringComparer.InvariantCultureIgnoreCase);
            var anyMembersHidden = !shouldShowPrivate && memberCountPublic.Result != memberCountAll.Result;
            
            await ctx.Paginate(
                members,
                memberCountDisplayed,
                5,
                embedTitle,
                (eb, ms) => {
                    foreach (var m in ms) {
                        var profile = $"**ID**: {m.Hid}";
                        if (m.Pronouns != null) profile += $"\n**Pronouns**: {m.Pronouns}";
                        if (m.Birthday != null) profile += $"\n**Birthdate**: {m.BirthdayString}";
                        if (m.ProxyTags.Count > 0) profile += $"\n**Proxy tags:** {m.ProxyTagsString()}";
                        if (m.Description != null) profile += $"\n\n{m.Description}";
                        if (m.MemberPrivacy == PrivacyLevel.Private)
                            profile += "*(this member is private)*";
                        
                        eb.AddField(m.Name, profile.Truncate(1024));
                    }

                    var footer = $"{memberCountDisplayed} total.";
                    if (anyMembersHidden && authCtx == LookupContext.ByOwner)
                        footer += " Private members have been hidden. type \"pk;system list full all\" to include them.";
                    eb.WithFooter(footer);
                    return Task.CompletedTask;
                }
            );
        }
        
        public async Task SystemFronter(Context ctx, PKSystem system)
        {
            if (system == null) throw Errors.NoSystemError;
            ctx.CheckSystemPrivacy(system, system.FrontPrivacy);
            
            var sw = await _data.GetLatestSwitch(system);
            if (sw == null) throw Errors.NoRegisteredSwitches;
            
            await ctx.Reply(embed: await _embeds.CreateFronterEmbed(sw, system.Zone));
        }

        struct FrontHistoryEntry
        {
            public Instant? LastTime;
            public PKSwitch ThisSwitch;

            public FrontHistoryEntry(Instant? lastTime, PKSwitch thisSwitch)
            {
                LastTime = lastTime;
                ThisSwitch = thisSwitch;
            }
        }
        
        public async Task SystemFrontHistory(Context ctx, PKSystem system)
        {
            if (system == null) throw Errors.NoSystemError;
            ctx.CheckSystemPrivacy(system, system.FrontHistoryPrivacy);

            var sws = _data.GetSwitches(system)
                .Scan(new FrontHistoryEntry(null, null), (lastEntry, newSwitch) => new FrontHistoryEntry(lastEntry.ThisSwitch?.Timestamp, newSwitch));
            var totalSwitches = await _data.GetSwitchCount(system);
            if (totalSwitches == 0) throw Errors.NoRegisteredSwitches;
            
            var embedTitle = system.Name != null ? $"Front history of {system.Name} (`{system.Hid}`)" : $"Front history of `{system.Hid}`";

            await ctx.Paginate(
                sws,
                totalSwitches,
                10,
                embedTitle,
                async (builder, switches) =>
                {
                    var outputStr = "";
                    foreach (var entry in switches)
                    {
                        var lastSw = entry.LastTime;

                        var sw = entry.ThisSwitch;
                        // Fetch member list and format
                        var members = await _data.GetSwitchMembers(sw).ToListAsync();
                        var membersStr = members.Any() ? string.Join(", ", members.Select(m => m.Name)) : "no fronter";

                        var switchSince = SystemClock.Instance.GetCurrentInstant() - sw.Timestamp;

                        // If this isn't the latest switch, we also show duration
                        string stringToAdd;
                        if (lastSw != null)
                        {
                            // Calculate the time between the last switch (that we iterated - ie. the next one on the timeline) and the current one
                            var switchDuration = lastSw.Value - sw.Timestamp;
                            stringToAdd =
                                $"**{membersStr}** ({Formats.ZonedDateTimeFormat.Format(sw.Timestamp.InZone(system.Zone))}, {Formats.DurationFormat.Format(switchSince)} ago, for {Formats.DurationFormat.Format(switchDuration)})\n";
                        }
                        else
                        {
                            stringToAdd =
                                $"**{membersStr}** ({Formats.ZonedDateTimeFormat.Format(sw.Timestamp.InZone(system.Zone))}, {Formats.DurationFormat.Format(switchSince)} ago)\n";
                        }

                        if (outputStr.Length + stringToAdd.Length > EmbedBuilder.MaxDescriptionLength) break;
                        outputStr += stringToAdd;
                    }

                    builder.Description = outputStr;
                }
            );
        }
        
        public async Task SystemFrontPercent(Context ctx, PKSystem system)
        {
            if (system == null) throw Errors.NoSystemError;
            ctx.CheckSystemPrivacy(system, system.FrontHistoryPrivacy);

            string durationStr = ctx.RemainderOrNull() ?? "30d";
            
            var now = SystemClock.Instance.GetCurrentInstant();

            var rangeStart = PluralKit.Utils.ParseDateTime(durationStr, true, system.Zone);
            if (rangeStart == null) throw Errors.InvalidDateTime(durationStr);
            if (rangeStart.Value.ToInstant() > now) throw Errors.FrontPercentTimeInFuture;
            
            var frontpercent = await _data.GetFrontBreakdown(system, rangeStart.Value.ToInstant(), now);
            await ctx.Reply(embed: await _embeds.CreateFrontPercentEmbed(frontpercent, system.Zone));
        }

        public async Task SystemProxy(Context ctx)
        {
            ctx.CheckSystem().CheckGuildContext();
            var gs = await _data.GetSystemGuildSettings(ctx.System, ctx.Guild.Id);

            bool newValue;
            if (ctx.Match("on", "enabled", "true", "yes")) newValue = true;
            else if (ctx.Match("off", "disabled", "false", "no")) newValue = false;
            else if (ctx.HasNext()) throw new PKSyntaxError("You must pass either \"on\" or \"off\".");
            else newValue = !gs.ProxyEnabled;

            gs.ProxyEnabled = newValue;
            await _data.SetSystemGuildSettings(ctx.System, ctx.Guild.Id, gs);

            if (newValue)
                await ctx.Reply($"Message proxying in this server ({ctx.Guild.Name.EscapeMarkdown()}) is now **enabled** for your system.");
            else
                await ctx.Reply($"Message proxying in this server ({ctx.Guild.Name.EscapeMarkdown()}) is now **disabled** for your system.");
        }
        
        public async Task SystemTimezone(Context ctx)
        {
            if (ctx.System == null) throw Errors.NoSystemError;

            var zoneStr = ctx.RemainderOrNull();
            if (zoneStr == null)
            {
                ctx.System.UiTz = "UTC";
                await _data.SaveSystem(ctx.System);
                await ctx.Reply($"{Emojis.Success} System time zone cleared.");
                return;
            }

            var zone = await FindTimeZone(ctx, zoneStr);
            if (zone == null) throw Errors.InvalidTimeZone(zoneStr);

            var currentTime = SystemClock.Instance.GetCurrentInstant().InZone(zone);
            var msg = await ctx.Reply(
                $"This will change the system time zone to {zone.Id}. The current time is {Formats.ZonedDateTimeFormat.Format(currentTime)}. Is this correct?");
            if (!await ctx.PromptYesNo(msg)) throw Errors.TimezoneChangeCancelled;
            ctx.System.UiTz = zone.Id;
            await _data.SaveSystem(ctx.System);

            await ctx.Reply($"System time zone changed to {zone.Id}.");
        }

        public async Task SystemPrivacy(Context ctx)
        {
            ctx.CheckSystem();

            if (!ctx.HasNext())
            {
                string PrivacyLevelString(PrivacyLevel level) => level switch
                {
                    PrivacyLevel.Private => "**Private** (visible only when queried by you)",
                    PrivacyLevel.Public => "**Public** (visible to everyone)",
                    _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
                };

                var eb = new EmbedBuilder()
                    .WithTitle("Current privacy settings for your system")
                    .AddField("Description", PrivacyLevelString(ctx.System.DescriptionPrivacy))
                    .AddField("Member list", PrivacyLevelString(ctx.System.MemberListPrivacy))
                    .AddField("Current fronter(s)", PrivacyLevelString(ctx.System.FrontPrivacy))
                    .AddField("Front/switch history", PrivacyLevelString(ctx.System.FrontHistoryPrivacy))
                    .WithDescription("To edit privacy settings, use the command:\n`pk;system privacy <subject> <level>`\n\n- `subject` is one of `description`, `list`, `front` or `fronthistory`\n- `level` is either `public` or `private`.");
                await ctx.Reply(embed: eb.Build());
                return;
            }
            
            PrivacyLevel PopPrivacyLevel(string subject, out string levelStr, out string levelExplanation)
            {
                if (ctx.Match("public", "show", "shown", "visible"))
                {
                    levelStr = "public";
                    levelExplanation = "be able to query";
                    return PrivacyLevel.Public;
                }

                if (ctx.Match("private", "hide", "hidden"))
                {
                    levelStr = "private";
                    levelExplanation = "*not* be able to query";
                    return PrivacyLevel.Private;
                }

                if (!ctx.HasNext())
                    throw new PKSyntaxError($"You must pass a privacy level for `{subject}` (`public` or `private`)");
                throw new PKSyntaxError($"Invalid privacy level `{ctx.PopArgument().SanitizeMentions()}` (must be `public` or `private`).");
            }

            string levelStr, levelExplanation, subjectStr;
            var subjectList = "`description`, `members`, `front` or `fronthistory`";
            if (ctx.Match("description", "desc", "text", "info"))
            {
                subjectStr = "description";
                ctx.System.DescriptionPrivacy = PopPrivacyLevel("description", out levelStr, out levelExplanation);
            } 
            else if (ctx.Match("members", "memberlist", "list", "mlist"))
            {
                subjectStr = "member list";
                ctx.System.MemberListPrivacy = PopPrivacyLevel("members", out levelStr, out levelExplanation);
            }
            else if (ctx.Match("front", "fronter"))
            {
                subjectStr = "fronter(s)";
                ctx.System.FrontPrivacy = PopPrivacyLevel("front", out levelStr, out levelExplanation);
            } 
            else if (ctx.Match("switch", "switches", "fronthistory", "fh"))
            {
                subjectStr = "front history";
                ctx.System.FrontHistoryPrivacy = PopPrivacyLevel("fronthistory", out levelStr, out levelExplanation);
            }
            else
                throw new PKSyntaxError($"Invalid privacy subject `{ctx.PopArgument().SanitizeMentions()}` (must be {subjectList}).");

            await _data.SaveSystem(ctx.System);
            await ctx.Reply($"System {subjectStr} privacy has been set to **{levelStr}**. Other accounts will now {levelExplanation} your system {subjectStr}.");
        }

        public async Task<DateTimeZone> FindTimeZone(Context ctx, string zoneStr) {
            // First, if we're given a flag emoji, we extract the flag emoji code from it.
            zoneStr = PluralKit.Utils.ExtractCountryFlag(zoneStr) ?? zoneStr;
            
            // Then, we find all *locations* matching either the given country code or the country name.
            var locations = TzdbDateTimeZoneSource.Default.Zone1970Locations;
            var matchingLocations = locations.Where(l => l.Countries.Any(c =>
                string.Equals(c.Code, zoneStr, StringComparison.InvariantCultureIgnoreCase) ||
                string.Equals(c.Name, zoneStr, StringComparison.InvariantCultureIgnoreCase)));
            
            // Then, we find all (unique) time zone IDs that match.
            var matchingZones = matchingLocations.Select(l => DateTimeZoneProviders.Tzdb.GetZoneOrNull(l.ZoneId))
                .Distinct().ToList();
            
            // If the set of matching zones is empty (ie. we didn't find anything), we try a few other things.
            if (matchingZones.Count == 0)
            {
                // First, we try to just find the time zone given directly and return that.
                var givenZone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneStr);
                if (givenZone != null) return givenZone;

                // If we didn't find anything there either, we try parsing the string as an offset, then
                // find all possible zones that match that offset. For an offset like UTC+2, this doesn't *quite*
                // work, since there are 57(!) matching zones (as of 2019-06-13) - but for less populated time zones
                // this could work nicely.
                var inputWithoutUtc = zoneStr.Replace("UTC", "").Replace("GMT", "");

                var res = OffsetPattern.CreateWithInvariantCulture("+H").Parse(inputWithoutUtc);
                if (!res.Success) res = OffsetPattern.CreateWithInvariantCulture("+H:mm").Parse(inputWithoutUtc);

                // If *this* didn't parse correctly, fuck it, bail.
                if (!res.Success) return null;
                var offset = res.Value;

                // To try to reduce the count, we go by locations from the 1970+ database instead of just the full database
                // This elides regions that have been identical since 1970, omitting small distinctions due to Ancient History(tm).
                var allZones = TzdbDateTimeZoneSource.Default.Zone1970Locations.Select(l => l.ZoneId).Distinct();
                matchingZones = allZones.Select(z => DateTimeZoneProviders.Tzdb.GetZoneOrNull(z))
                    .Where(z => z.GetUtcOffset(SystemClock.Instance.GetCurrentInstant()) == offset).ToList();
            }
            
            // If we have a list of viable time zones, we ask the user which is correct.
            
            // If we only have one, return that one.
            if (matchingZones.Count == 1)
                return matchingZones.First();
            
            // Otherwise, prompt and return!
            return await ctx.Choose("There were multiple matches for your time zone query. Please select the region that matches you the closest:", matchingZones,
                z =>
                {
                    if (TzdbDateTimeZoneSource.Default.Aliases.Contains(z.Id))
                        return $"**{z.Id}**, {string.Join(", ", TzdbDateTimeZoneSource.Default.Aliases[z.Id])}";

                    return $"**{z.Id}**";
                });
        }
    }
}
