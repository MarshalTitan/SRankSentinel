using System.Globalization;
using System.Numerics;

namespace SRankSentinel;

/// <summary>
/// Faloop's zone POIs are private feed identifiers, not game row IDs. These values are a
/// reviewed snapshot of the ShB/EW/DT mob POIs in Faloop's public web client
/// (main.a8fa335a5cf92d82.js, verified 2026-09-05). The resulting
/// map flag is only an approach hint; Sentinel still identifies and measures from the live mark.
/// </summary>
internal static class FaloopCatalog
{
    private sealed record ZoneDefinition(uint TerritoryId, IReadOnlyDictionary<int, Vector2> MobPois);

    private static readonly IReadOnlyDictionary<string, ZoneDefinition> Zones = BuildZones();

    public static bool TryResolve(string? zoneId, int poiId, out uint territoryId, out float mapX, out float mapY)
    {
        territoryId = 0;
        mapX = 0;
        mapY = 0;
        if (string.IsNullOrWhiteSpace(zoneId))
            return false;

        var key = NormalizeSlug(zoneId);
        var zone = Zones.TryGetValue(key, out var bySlug)
            ? bySlug
            : Zones.Values.FirstOrDefault(candidate => candidate.TerritoryId.ToString(CultureInfo.InvariantCulture) == key);
        if (zone is null || !zone.MobPois.TryGetValue(poiId, out var point))
            return false;

        territoryId = zone.TerritoryId;
        mapX = point.X;
        mapY = point.Y;
        return true;
    }

    public static bool TryResolveTerritory(string? zoneId, out uint territoryId)
    {
        territoryId = 0;
        if (string.IsNullOrWhiteSpace(zoneId))
            return false;
        var key = NormalizeSlug(zoneId);
        if (Zones.TryGetValue(key, out var zone))
        {
            territoryId = zone.TerritoryId;
            return true;
        }
        if (!uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTerritoryId) ||
            !Zones.Values.Any(candidate => candidate.TerritoryId == parsedTerritoryId))
            return false;

        territoryId = parsedTerritoryId;
        return true;
    }

    public static string DisplayName(string slug)
    {
        var value = slug.Replace('_', ' ').Replace('-', ' ').Trim();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
    }

    private static IReadOnlyDictionary<string, ZoneDefinition> BuildZones() => new Dictionary<string, ZoneDefinition>(StringComparer.Ordinal)
    {
        ["lakeland"] = Zone(813, "892:19.8,9.6;893:12.4,10.5;894:32.9,12;895:36.7,12.2;896:23,12.3;897:11.7,12.8;898:27.5,15.5;899:35.3,16;900:11.6,17.2;901:29.5,19.1;902:23.3,22.1;903:30.9,22.2;904:8,22.9;905:18.5,23;906:25.5,23.8;907:14,24.7;908:11.1,24.9;909:35.9,26.8;910:23,29.7;911:27.6,30.5;912:35.2,32.1;913:29.9,36.1;914:26.9,37.4"),
        ["kholusia"] = Zone(814, "921:16.8,6.9;922:34.3,10.5;923:19.7,10.6;924:24.9,11.4;925:22,14.1;926:12.2,15.1;927:24,15.3;928:15,15.8;929:22.8,17.4;930:11.6,18.6;931:26.6,19.2;932:31.2,19.6;933:33.4,21.4;934:21.3,22.8;935:26.8,24.1;936:15.1,24.2;937:34.5,24.5;938:9.4,25.4;939:8.8,28.9;940:29.9,29.8;941:24.5,30.2;942:21,31.1;943:33.8,32.3"),
        ["amharaeng"] = Zone(815, "951:30.4,9.8;952:22.6,10;953:16.7,10.1;954:10.3,11.7;955:13.6,11.8;956:28.5,12.5;957:30.8,13.8;958:19.2,16.1;959:11.6,19.3;960:28.8,20.3;961:33.5,21.6;962:16.5,24;963:19.2,24.7;964:30.1,24.7;965:28.5,26.1;966:19.7,28.9;967:23.3,29.8;968:14.1,31.9;969:17,31.7;970:32.8,33.8;971:30.5,35.1;972:27.5,35.1;973:19.9,36.4"),
        ["ilmheg"] = Zone(816, "978:29.2,5.3;979:25.5,6.8;980:34.2,7.3;981:20,8.5;982:32.1,11.2;983:31.5,13.7;984:11.1,15.8;985:27.1,18.9;986:10.5,20.1;987:25,22.1;988:7.5,22.8;989:13.4,22.9;990:8.1,27.1;991:19.3,27.4;992:23.1,28.7;993:5.7,29.6;994:9.7,30.6;995:24.2,32.7;996:13.8,34.2;997:19.8,34.8;998:23.5,35.6;999:24.9,37.3"),
        ["theraktikagreatwood"] = Zone(817, "1007:9.5,18.5;1008:14.4,22.2;1009:18.9,22.3;1011:7.4,22.8;1012:9.7,24.1;1014:17,24.3;1016:15,30.2;1017:25,30.3;1018:8.4,34.5;1019:17.7,34.9;1020:12.2,35.7;1021:14.5,36.6;1022:24.4,37.2"),
        ["thetempest"] = Zone(818, "1026:31,4;1027:11.2,4.8;1028:8.4,7.2;1029:21.3,7.3;1030:28.8,8.4;1031:8.9,8.8;1032:25.8,9.6;1033:15.6,10.6;1034:30.9,11.1;1035:36.7,11.5;1036:25.2,12.6;1037:18,13.4;1038:37.7,14;1039:37.6,16.4;1040:13.5,17.3;1041:36,19.7;1042:15.6,19.9;1043:33.8,21.7;1044:12.9,22.2;1045:29.1,23;1046:26.8,24.7;1047:24.7,25;1048:27,26.4;1049:33.5,29.9"),
        ["labyrinthos"] = Zone(956, "1054:25.1,8.5;1277:21.6,8.5;1055:29.9,8.2;1056:16.9,9.6;1057:34.2,13.5;1058:24.9,16;1059:16.7,16.8;1060:35,17.9;1061:10.7,19.2;1062:9.3,22.1;1063:25.4,24.9;1064:32.3,25.9;1065:26.3,32.8;1066:5.9,33.5;1067:12.1,35.2;1068:19.6,38.5"),
        ["thavnair"] = Zone(957, "1073:22.9,10.4;1074:18.5,11.5;1075:14.5,12.2;1280:6.7,12.9;1076:29.5,13.7;1077:12.1,16.2;1078:17.9,16.4;1079:24.2,16.8;1080:32.5,20.1;1081:26.7,20.9;1082:18.4,23.6;1083:33.2,24.9;1084:27.7,25.5;1085:16.5,29.4;1086:20.5,31.3;1087:9.3,37.7"),
        ["garlemald"] = Zone(958, "1274:26.8,7.9;1091:32.1,9;1092:17.7,10;1093:9.9,11.6;1094:12.1,12.8;1095:11.8,17.2;1096:15.9,19.7;1097:29.1,20.8;1098:33.1,21.9;1099:20.3,23.7;1100:23.4,25.8;1101:33.3,28.7;1102:32.5,32.5;1103:22.4,32.6;1104:27.6,34"),
        ["marelamentorum"] = Zone(959, "1108:11.9,20.6;1109:18.5,21.7;1110:33,23.4;1111:24.3,23.4;1112:10.4,24.1;1113:17.3,24.8;1114:28.2,26.8;1115:36.4,27;1116:16.5,28.8;1117:30,30;1118:18.5,30.2;1119:24.2,33.4;1120:20.9,34.6;1121:29.2,35.3;1122:11.7,35.8"),
        ["ultimathule"] = Zone(960, "1126:19.3,9.7;1127:32.2,10;1128:13.2,10.4;1129:27.9,12.3;1130:16.1,16.8;1131:8.3,20.3;1132:34.5,21.4;1133:12,21.9;1134:16.4,26;1135:14.5,29.6;1136:17.6,30.3;1137:10.7,31.6;1276:15.4,32.4;1138:23.4,33;1139:21.1,34.1;1140:14.9,36.1"),
        ["elpis"] = Zone(961, "1145:21.7,6.2;1146:28.9,7.3;1147:16.8,7;1148:12.5,9.9;1149:33.8,10.7;1150:37.7,13.3;1151:21.3,13.3;1152:34.2,14.1;1153:32.7,18.4;1154:22.7,19.5;1155:18.5,24.5;1156:29.7,27.4;1157:6.9,29;1158:18,30.2;1159:12.9,32.2;1160:7.9,35.7"),
        ["urqopacha"] = Zone(1187, "1165:11.4,9.1;1166:28.7,9.3;1167:18.8,14;1168:21.6,16.7;1169:33.9,19;1170:21.6,20.4;1171:28.1,22.4;1275:20.6,23.6;1172:15.7,24.1;1173:7.4,25.4;1174:25.7,13.9;1175:18.3,17.9;1176:34.5,28.2;1177:15.6,28.5;1178:25.9,27.9"),
        ["kozamauka"] = Zone(1188, "1184:9.2,7.9;1185:6.6,11.9;1279:29.7,15.7;1187:16.3,17.2;1186:36.8,20.4;1188:15.7,23.4;1189:20.3,28.4;1190:5.2,28.7;1191:34.2,36.2;1192:24,36.5;1193:16.7,7.5;1194:33.1,8.2;1195:29.5,24.5;1196:15.8,32.6;1197:13.8,14.8"),
        ["yaktel"] = Zone(1189, "1281:13.7,5.4;1201:26.4,9.6;1202:23.4,14.2;1203:33.2,16.2;1204:8.5,19.4;1205:6.3,26.4;1206:13.5,25.7;1207:21.7,28.4;1208:24.7,33;1209:21.3,36.3;1210:17.1,13.9;1211:35.4,22.4;1212:27.9,24.7;1213:12.7,35.7;1214:29.7,18.9"),
        ["shaaloani"] = Zone(1190, "1220:34.3,6.5;1221:16.1,8.3;1222:9,16.5;1223:23.1,18.9;1282:36,19.1;1224:25.2,23.2;1225:31.4,23.3;1226:13.3,27.3;1227:22.1,28.1;1228:21.7,33.3;1229:22.5,5.1;1230:11.5,8.4;1231:23.3,13.3;1232:14.9,30.8;1233:34.3,31.6;1234:13.3,13.3"),
        ["heritagefound"] = Zone(1191, "1240:36.9,12.7;1241:27.1,13.8;1242:29.6,19.5;1243:24.2,19.7;1244:12.7,20.7;1245:14.6,26;1246:29.6,29.8;1247:27.5,33.6;1248:8.2,33.3;1278:13.7,37.7;1249:30,9.7;1250:14,17.8;1251:32.2,22.7;1252:15,34.6;1253:17.5,20.3"),
        ["livingmemory"] = Zone(1192, "1258:5.2,12.8;1259:37.2,18.7;1260:19,20.1;1261:32.8,20.9;1262:12.9,27.7;1263:4.3,28.9;1264:38.9,30.1;1265:26.9,31.2;1283:4.3,33.1;1266:12.4,37.7;1267:37.3,33.4;1268:27.3,7.1;1269:11.5,18.1;1270:19.7,30.7;1271:28.5,36.4;1272:34.4,26.3"),
    };

    private static ZoneDefinition Zone(uint territoryId, string points)
    {
        var result = new Dictionary<int, Vector2>();
        foreach (var item in points.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idAndPoint = item.Split(':', 2);
            var coordinates = idAndPoint[1].Split(',', 2);
            result[int.Parse(idAndPoint[0], CultureInfo.InvariantCulture)] = new Vector2(
                float.Parse(coordinates[0], CultureInfo.InvariantCulture),
                float.Parse(coordinates[1], CultureInfo.InvariantCulture));
        }
        return new ZoneDefinition(territoryId, result);
    }

    private static string NormalizeSlug(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
