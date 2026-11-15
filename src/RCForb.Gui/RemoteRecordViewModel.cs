using RCForb.Client.Directory;

namespace RCForb.Gui;

public sealed class RemoteRecordViewModel : ObservableObject
{
    private readonly RemoteRecord _record;

    public RemoteRecordViewModel(RemoteRecord record) => _record = record;

    public RemoteRecord Record => _record;

    public string DisplayName => $"{_record.ServerName} / {_record.RadioName}";
    public string Detail => $"{_record.Country}  •  {_record.ListenPolicy}  •  {(_record.Transport == TransportGeneration.V07 ? "v0.7" : "v0.9")}";
    public string Endpoint => _record.Endpoint;
    public bool IsOnline => _record.Online;
    public bool IsV07 => _record.Transport == TransportGeneration.V07;
    public bool IsPublicListen => _record.IsPublicListen;
}
