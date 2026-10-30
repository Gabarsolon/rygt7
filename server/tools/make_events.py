"""Builds RyGTServer's starter Sport Mode events from GT Sport's own GameParameter files.

GT Sport ships its event definitions as GAME_PARAMETER\\GT7SP\\EVENT\\R*.JSON; extract that folder
from the game's volume first. This writes one GameParameter file per event plus schedule.json,
which tells RyGTServer what each Sport Mode screen shows:

  Time Trial (event folder 22)  three ONLINE_TIME_ATTACK events, copies of the game's own online
                                time trial (R001.JSON) moved to other tracks and cars
  Daily Race A/B/C (channels 1-3)  three SPORT_DAILY_RACE events, copies of the game's two-lap GT3
                                races (R127.JSON)

Usage: make_events.py <GAME_PARAMETER folder> <server-data\\events folder>"""
import copy, json, os, sys

TIME_TRIAL_FOLDER = 22  # OnlineTimeAttackUtil::EVENT_FOLDER_ID in the game's scripts
LANGUAGES = ("US", "GB", "JP", "FR", "DE", "IT", "ES", "PT", "NL", "RU", "TW", "KR", "TR", "PL", "CZ", "EL", "SE", "MS", "BP", "MX", "AR", "TH", "ID")

# (event id, course code, car label, title)
TIME_TRIALS = [
    (9100001, "suzuka", "m6_gt3_m_power_16", "Suzuka Circuit"),
    (9100002, "interlagos_GP", "_650s_gt3_15", "Interlagos"),
    (9100003, "mount_panorama", "corvette_c7_gt3m_xx", "Mount Panorama"),
]
# (event id, Daily Race channel, source event in R127.JSON, title)
DAILY_RACES = [
    (9000001, 1, 1002389, "Daily Race A - Tokyo Expressway"),
    (9000002, 2, 1002388, "Daily Race B - Suzuka"),
    (9000003, 3, 1002382, "Daily Race C - Mount Panorama"),
]


def load(root, name):
    return json.load(open(os.path.join(root, "GT7SP", "EVENT", name), encoding="utf-8"))["GameParameter"]


def find_event(gp, event_id):
    return next(e for e in gp["events"] if e["event_id"] == event_id)


def track_for(course, sources):
    """A track entry for the course, taken from a real event that races on it."""
    for gp in sources:
        for e in gp["events"]:
            track = gp["tracks"][e.get("track_index", 0)]
            if track["course_code"] == course:
                return copy.deepcopy(track)
    raise SystemExit("no event in the source files races on " + course)


def set_car(event, label):
    for entry_set in event["entry_set"]:
        for base in entry_set.get("entry_generate", {}).get("player_entry_base_array", []):
            base["car"]["label"] = label
        for entry in entry_set.get("entry", []):
            if entry.get("player_no", -1) == 0 and "car" in entry:
                entry["car"]["label"] = label


def finish(event, event_id, game_mode, title):
    event["event_id"] = event_id
    event["game_mode"] = game_mode
    event["track_index"] = 0
    event["ranking"] = {"board_id": event_id, "registration": 0}
    event["information"]["title"] = {lang: title for lang in LANGUAGES}
    for key in ("one_line_title", "description"):
        if key in event["information"]:
            event["information"][key] = {lang: "" for lang in LANGUAGES}


def write(out, gp, folder_id, event, track):
    data = {"GameParameter": {"version": gp["version"], "championship": 0, "folder_id": folder_id,
                              "events": [event], "series_reward": gp.get("series_reward", {}), "tracks": [track]}}
    path = os.path.join(out, "%d.json" % event["event_id"])
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
    print("%s  %s" % (path, event["information"]["title"]["US"]))


def main():
    root, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    r001, r127 = load(root, "R001.JSON"), load(root, "R127.JSON")
    schedule = {"daily_channels": {}, "folders": {str(TIME_TRIAL_FOLDER): []}, "required_time": {}}

    template = next(e for e in r001["events"] if e["game_mode"] == "ONLINE_TIME_ATTACK")
    for event_id, course, car, title in TIME_TRIALS:
        event = copy.deepcopy(template)
        set_car(event, car)
        finish(event, event_id, "ONLINE_TIME_ATTACK", "Time Trial - " + title)
        write(out, r001, TIME_TRIAL_FOLDER, event, track_for(course, (r127, r001)))
        schedule["folders"][str(TIME_TRIAL_FOLDER)].append(event_id)

    for event_id, channel, source_id, title in DAILY_RACES:
        source = find_event(r127, source_id)
        event = copy.deepcopy(source)
        track = copy.deepcopy(r127["tracks"][source.get("track_index", 0)])
        finish(event, event_id, "SPORT_DAILY_RACE", title)
        write(out, r127, 0, event, track)
        schedule["daily_channels"][str(channel)] = event_id
        schedule["required_time"][str(event_id)] = 600

    with open(os.path.join(out, "schedule.json"), "w", encoding="utf-8") as f:
        json.dump(schedule, f, indent=2)
    print(os.path.join(out, "schedule.json"))


main()
