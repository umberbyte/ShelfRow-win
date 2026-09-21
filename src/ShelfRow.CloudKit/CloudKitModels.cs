using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShelfRow.CloudKit;

public class CKZoneID
{
    [JsonPropertyName("zoneName")]
    public string ZoneName { get; set; } = "com.apple.coredata.cloudkit.zone";

    [JsonPropertyName("ownerRecordName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerRecordName { get; set; }
}

public class CKRecordField
{
    [JsonPropertyName("value")]
    public object? Value { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    public CKRecordField() { }
    public CKRecordField(object? value) { Value = value; }
}

public class CKRecordReference
{
    [JsonPropertyName("recordName")]
    public string RecordName { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Action { get; set; }
}

public class CKRecord
{
    [JsonPropertyName("recordName")]
    public string RecordName { get; set; } = string.Empty;

    [JsonPropertyName("recordType")]
    public string RecordType { get; set; } = string.Empty;

    [JsonPropertyName("recordChangeTag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecordChangeTag { get; set; }

    [JsonPropertyName("fields")]
    public Dictionary<string, CKRecordField> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("deleted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Deleted { get; set; }

    /// <summary>
    /// Set on a record in a modify response that the server refused. A write can fail
    /// for one record while the rest of the batch succeeds, so the failure is reported
    /// per record rather than for the request.
    /// </summary>
    [JsonPropertyName("serverErrorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerErrorCode { get; set; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }
}

public class CKChangesZoneRequest
{
    [JsonPropertyName("zones")]
    public List<CKZoneRequestItem> Zones { get; set; } = new();
}

public class CKZoneRequestItem
{
    [JsonPropertyName("zoneID")]
    public CKZoneID ZoneID { get; set; } = new();

    [JsonPropertyName("syncToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SyncToken { get; set; }

    [JsonPropertyName("resultsLimit")]
    public int ResultsLimit { get; set; } = 200;
}

public class CKErrorResponse
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("serverErrorCode")]
    public string? ServerErrorCode { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("redirectURL")]
    public string? RedirectURL { get; set; }
}

public class CKChangesZoneResponse
{
    [JsonPropertyName("zones")]
    public List<CKZoneResponseItem>? Zones { get; set; }
}

public class CKZoneResponseItem
{
    [JsonPropertyName("zoneID")]
    public CKZoneID? ZoneID { get; set; }

    [JsonPropertyName("syncToken")]
    public string? SyncToken { get; set; }

    [JsonPropertyName("moreComing")]
    public bool MoreComing { get; set; }

    [JsonPropertyName("records")]
    public List<CKRecord>? Records { get; set; }

    [JsonPropertyName("serverErrorCode")]
    public string? ServerErrorCode { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("redirectURL")]
    public string? RedirectURL { get; set; }
}

public class CKModifyRecordsRequest
{
    /// <summary>
    /// Without this the server writes to the default zone, where the Mac app never looks.
    /// </summary>
    [JsonPropertyName("zoneID")]
    public CKZoneID? ZoneID { get; set; }

    [JsonPropertyName("operations")]
    public List<CKRecordOperation> Operations { get; set; } = new();
}

public class CKRecordOperation
{
    [JsonPropertyName("operationType")]
    public string OperationType { get; set; } = "update"; // "create", "update", "forceUpdate", "delete"

    [JsonPropertyName("record")]
    public object Record { get; set; } = new CKRecord();
}

/// <summary>CloudKit's delete contract accepts only the record name.</summary>
public class CKDeleteRecord
{
    [JsonPropertyName("recordName")]
    public string RecordName { get; set; } = string.Empty;
}

public class CKModifyRecordsResponse
{
    [JsonPropertyName("records")]
    public List<CKRecord>? Records { get; set; }
}

public class CKModifyZonesRequest
{
    [JsonPropertyName("operations")]
    public List<CKZoneOperation> Operations { get; set; } = new();
}

public class CKZoneOperation
{
    [JsonPropertyName("operationType")]
    public string OperationType { get; set; } = "delete";

    [JsonPropertyName("zone")]
    public CKZone Zone { get; set; } = new();
}

public class CKZone
{
    [JsonPropertyName("zoneID")]
    public CKZoneID ZoneID { get; set; } = new();
}

public class CKModifyZonesResponse
{
    [JsonPropertyName("zones")]
    public List<CKZoneResponseItem>? Zones { get; set; }
}
