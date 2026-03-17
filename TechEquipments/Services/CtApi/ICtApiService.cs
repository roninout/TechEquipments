using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CtApi
{
    public interface ICtApiService
    {
        Task OpenAsync(string ip = null, string user = null, string password = null);
        Task<string> TagReadAsync(string tagName);
        Task<object> TagWriteAsync(string tag, string value);
        Task<IEnumerable<Dictionary<string, string>>> FindAsync(string tableName, string filter, string cluster, params string[] propertiesName);
        Task<string> CicodeAsync(string cmd);
        Task<bool> IsConnected();
        string UserInfo(int type);

        void SetCtApiDirectory(string path);

        Task<List<TrnData>> GetTrnData(string tagName, DateTime startTime, DateTime endTime);

        // ===== Connection state for UI =====
        bool IsConnectionAvailable { get; }
        string? LastConnectionError { get; }

        /// <summary>
        /// isConnected = true  -> связь доступна / восстановлена
        /// isConnected = false -> связь потеряна
        /// message -> текст для UI
        /// </summary>
        event Action<bool, string?>? ConnectionStateChanged;
    }
}
