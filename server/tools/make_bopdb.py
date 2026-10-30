"""Builds an empty BoP database for GT Sport's /event/bopdb.dat.

The game's UTIL script opens the download with pdistd::MSqlite and asks
  SELECT GainPower, GainWeight FROM BOP WHERE Boptype = ? AND CarTagID = ? AND CarCode = ? [AND SettingType = ?]
  SELECT GainPower, GainWeight FROM OVAL_BOP WHERE CarCode = ?
and when a car has no row it asks its own SPECDB instead, so empty tables mean every car keeps the
BoP shipped with the game. Usage: make_bopdb.py <output file>"""
import os, sqlite3, sys

out = sys.argv[1]
if os.path.exists(out):
    os.remove(out)
db = sqlite3.connect(out)
db.executescript("""
CREATE TABLE BOP (Boptype INTEGER, CarTagID INTEGER, CarCode INTEGER, SettingType INTEGER,
                  GainPower INTEGER, GainWeight INTEGER);
CREATE INDEX BOP_LOOKUP ON BOP (Boptype, CarTagID, CarCode);
CREATE TABLE OVAL_BOP (CarCode INTEGER PRIMARY KEY, GainPower INTEGER, GainWeight INTEGER);
""")
db.commit()
db.execute("PRAGMA journal_mode=DELETE")
db.close()
print(f"{out}: {os.path.getsize(out)} bytes")
