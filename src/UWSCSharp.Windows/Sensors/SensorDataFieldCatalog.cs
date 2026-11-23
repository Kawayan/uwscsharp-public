using System.Collections.Frozen;

namespace UWSCSharp.Windows;

/// <summary>
/// 原版の取得項目を、Windowsのセンサーreportの欄へ対応付けます。
/// </summary>
internal static class SensorDataFieldCatalog
{
    private static readonly FrozenDictionary<int, SensorDataField> Fields = Build();

    internal static bool TryGet(int selector, out SensorDataField field) =>
        Fields.TryGetValue(selector, out field);

    internal static Guid LocationCategory => Fields[60].CategoryId;

    internal static bool SupportsCategory(Guid category) =>
        Fields.Values.Any(field => field.CategoryId == category);

    private static FrozenDictionary<int, SensorDataField> Build()
    {
        var fields = new Dictionary<int, SensorDataField>();
        Add("ca19690f-a2c7-477d-a99e-99ec6e2b5648",
            "2299288a-6d9e-4b0b-b7ec-3528f89e40af",
            SensorDataValueKind.Boolean, (1, 2));
        Add("ca19690f-a2c7-477d-a99e-99ec6e2b5648",
            "2299288a-6d9e-4b0b-b7ec-3528f89e40af",
            SensorDataValueKind.Numeric, (2, 3));
        Add("fb73fcd8-fc4a-483c-ac58-27b691c6beff",
            "bbb246d1-e242-4780-a2d3-cded84f35842",
            SensorDataValueKind.Numeric,
            (5, 4), (6, 5), (7, 6), (8, 3), (9, 2), (10, 7));
        Add("323439aa-7f66-492b-ba0c-73e9aa0a65d5",
            "8b0aa2f1-2d57-42ee-8cc0-4d27622b46c4",
            SensorDataValueKind.Numeric,
            (15, 2), (16, 4), (17, 3), (18, 5), (19, 6));
        Add("17a665c0-9063-4216-b202-5c7a255e18ce",
            "e4c77ce2-dcb7-46e9-8439-4fec548833a6",
            SensorDataValueKind.Numeric, (20, 2), (21, 3), (22, 4));
        Add("8d131d68-8ef7-4656-80b5-cccbd93791c5",
            "38564a7c-f2f2-49bb-9b2b-ba60f66a58df",
            SensorDataValueKind.Numeric,
            (25, 4), (26, 5), (27, 6), (28, 8));
        Add("cd09daf1-3b2e-4c3d-b598-b5e5ff93fd46",
            "3f8a69a2-07c5-4e48-a965-cd797aab56d5",
            SensorDataValueKind.Numeric,
            (30, 2), (31, 3), (32, 4), (33, 5),
            (34, 6), (35, 7), (36, 8));
        Add("b000e77e-f5b5-420f-815d-0270a726f270",
            "d7a59a3c-3421-44ab-8d3a-9de8ab6c4cae",
            SensorDataValueKind.Integer, (40, 2));
        // 原版のWindows用バーコードgetterは、取得せず空文字列を返します。
        fields.Add(41, new SensorDataField(
            new Guid("b000e77e-f5b5-420f-815d-0270a726f270"),
            null, SensorDataValueKind.EmptyString));
        Add("9e6c04b6-96fe-4954-b726-68682a473f69",
            "1637d8a2-4248-4275-865d-558de84aedfd",
            SensorDataValueKind.Numeric,
            (45, 2), (46, 3), (47, 4), (48, 8), (49, 9),
            (50, 10), (51, 13), (52, 14), (53, 11), (54, 12));
        Add("bfa794e4-f964-4fdb-90f6-51056bfe4b44",
            "055c74d8-ca6f-47d6-95c6-1ed3637a0ff4",
            SensorDataValueKind.Numeric, (60, 4), (61, 2), (62, 3));
        // 位置センサーの速度も、原版は運動用の速度キーを指定します。
        Add("bfa794e4-f964-4fdb-90f6-51056bfe4b44",
            "3f8a69a2-07c5-4e48-a965-cd797aab56d5",
            SensorDataValueKind.Numeric, (63, 8));
        Add("bfa794e4-f964-4fdb-90f6-51056bfe4b44",
            "055c74d8-ca6f-47d6-95c6-1ed3637a0ff4",
            SensorDataValueKind.String,
            (64, 23), (65, 24), (66, 25), (67, 28));
        return fields.ToFrozenDictionary();

        void Add(string category, string format, SensorDataValueKind kind,
            params (int Selector, uint Property)[] entries)
        {
            var categoryId = Guid.Parse(category);
            var formatId = Guid.Parse(format);
            foreach ((int selector, uint property) in entries)
            {
                fields.Add(selector, new SensorDataField(categoryId,
                    new SensorPropertyKey(formatId, property), kind));
            }
        }
    }
}
