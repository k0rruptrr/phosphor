using Phosphor.Core;
using Phosphor.Data;

namespace Phosphor.App;

/// <summary>
/// Только для изолированной сборки сцены/фильтра (-p:OnlyScene / -p:OnlyFilter): источников нет,
/// данные — только из дискового кеша, если он уже накоплен полной сборкой. В обычной сборке не компилируется.
/// </summary>
internal static class DataWiring
{
    public static DataHub CreateHub(PhosphorConfig config, bool online, Log log)
    {
        var hub = new DataHub(Array.Empty<IDataSource>(), online: false, m => log.Info(m));
        hub.Start();
        log.Info("data: изолированная сборка — источники не подключены");
        return hub;
    }
}
