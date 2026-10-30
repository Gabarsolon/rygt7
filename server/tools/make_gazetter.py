"""Builds RyGTServer's /event/gazetter.dat, GT Sport's list of regions inside each country.

Sport Mode asks for it before showing the player's location, and stops with "Location information
could not be acquired" without it. The game's UTIL script opens it with pdistd::MSqlite and asks
  SELECT unid as region1, local_name FROM adm1s WHERE psn_cc = ? ORDER BY display_rank_local
  SELECT unid as region2, parent_unid as region1, local_name FROM adm2s WHERE parent_unid = ? ...
  SELECT local_name FROM adm1s WHERE unid = ?      (and the same for adm2s)
This gives the larger countries their states or provinces and every other country one region
covering all of it; each region has a single second-level entry of the same name.
Usage: make_gazetter.py <output file>"""
import os, sqlite3, sys

REGIONS = {
    "US": "Alabama Alaska Arizona Arkansas California Colorado Connecticut Delaware Florida Georgia Hawaii Idaho "
          "Illinois Indiana Iowa Kansas Kentucky Louisiana Maine Maryland Massachusetts Michigan Minnesota "
          "Mississippi Missouri Montana Nebraska Nevada New_Hampshire New_Jersey New_Mexico New_York "
          "North_Carolina North_Dakota Ohio Oklahoma Oregon Pennsylvania Rhode_Island South_Carolina "
          "South_Dakota Tennessee Texas Utah Vermont Virginia Washington West_Virginia Wisconsin Wyoming "
          "District_of_Columbia Puerto_Rico",
    "CA": "Alberta British_Columbia Manitoba New_Brunswick Newfoundland_and_Labrador Nova_Scotia Ontario "
          "Prince_Edward_Island Quebec Saskatchewan Northwest_Territories Nunavut Yukon",
    "AU": "Australian_Capital_Territory New_South_Wales Northern_Territory Queensland South_Australia "
          "Tasmania Victoria Western_Australia",
    "GB": "England Scotland Wales Northern_Ireland",
    "NZ": "Northland Auckland Waikato Bay_of_Plenty Gisborne Hawke's_Bay Taranaki Manawatu-Whanganui "
          "Wellington Tasman Nelson Marlborough West_Coast Canterbury Otago Southland",
    "DE": "Baden-Wurttemberg Bavaria Berlin Brandenburg Bremen Hamburg Hesse Lower_Saxony "
          "Mecklenburg-Vorpommern North_Rhine-Westphalia Rhineland-Palatinate Saarland Saxony "
          "Saxony-Anhalt Schleswig-Holstein Thuringia",
    "JP": "Hokkaido Aomori Iwate Miyagi Akita Yamagata Fukushima Ibaraki Tochigi Gunma Saitama Chiba Tokyo "
          "Kanagawa Niigata Toyama Ishikawa Fukui Yamanashi Nagano Gifu Shizuoka Aichi Mie Shiga Kyoto Osaka "
          "Hyogo Nara Wakayama Tottori Shimane Okayama Hiroshima Yamaguchi Tokushima Kagawa Ehime Kochi "
          "Fukuoka Saga Nagasaki Kumamoto Oita Miyazaki Kagoshima Okinawa",
}
# PSN countries without a list above get one region named after the country.
COUNTRIES = {
    "AE": "United Arab Emirates", "AR": "Argentina", "AT": "Austria", "BE": "Belgium", "BG": "Bulgaria",
    "BH": "Bahrain", "BO": "Bolivia", "BR": "Brazil", "CH": "Switzerland", "CL": "Chile", "CN": "China",
    "CO": "Colombia", "CR": "Costa Rica", "CY": "Cyprus", "CZ": "Czech Republic", "DK": "Denmark",
    "EC": "Ecuador", "ES": "Spain", "FI": "Finland", "FR": "France", "GR": "Greece", "GT": "Guatemala",
    "HK": "Hong Kong", "HN": "Honduras", "HR": "Croatia", "HU": "Hungary", "ID": "Indonesia",
    "IE": "Ireland", "IL": "Israel", "IN": "India", "IS": "Iceland", "IT": "Italy", "KR": "Korea",
    "KW": "Kuwait", "LB": "Lebanon", "LU": "Luxembourg", "MT": "Malta", "MX": "Mexico", "MY": "Malaysia",
    "NI": "Nicaragua", "NL": "Netherlands", "NO": "Norway", "OM": "Oman", "PA": "Panama", "PE": "Peru",
    "PH": "Philippines", "PL": "Poland", "PT": "Portugal", "PY": "Paraguay", "QA": "Qatar", "RO": "Romania",
    "RU": "Russia", "SA": "Saudi Arabia", "SE": "Sweden", "SG": "Singapore", "SI": "Slovenia",
    "SK": "Slovakia", "SV": "El Salvador", "TH": "Thailand", "TR": "Turkey", "TW": "Taiwan",
    "UA": "Ukraine", "UY": "Uruguay", "ZA": "South Africa",
}

out = sys.argv[1]
if os.path.exists(out):
    os.remove(out)
db = sqlite3.connect(out)
db.executescript("""
CREATE TABLE adm1s (unid INTEGER PRIMARY KEY, psn_cc TEXT, local_name TEXT, display_rank_local INTEGER);
CREATE INDEX adm1s_cc ON adm1s (psn_cc);
CREATE TABLE adm2s (unid INTEGER PRIMARY KEY, parent_unid INTEGER, local_name TEXT, display_rank_local INTEGER);
CREATE INDEX adm2s_parent ON adm2s (parent_unid);
""")
unid = 1000
lists = dict(REGIONS)
for cc, name in COUNTRIES.items():
    lists.setdefault(cc, name.replace(" ", "_"))
for cc in sorted(lists):
    for rank, name in enumerate(lists[cc].split(), 1):
        unid += 1
        name = name.replace("_", " ")
        db.execute("INSERT INTO adm1s VALUES (?, ?, ?, ?)", (unid, cc, name, rank))
        db.execute("INSERT INTO adm2s VALUES (?, ?, ?, ?)", (unid * 100 + 1, unid, name, 1))
db.commit()
db.execute("PRAGMA journal_mode=DELETE")
db.close()
print(f"{out}: {unid - 1000} regions, {os.path.getsize(out)} bytes")
