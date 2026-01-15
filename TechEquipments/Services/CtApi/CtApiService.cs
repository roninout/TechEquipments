using DevExpress.XtraPrinting.DataNodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CtApi
{
    public class CtApiService : ICtApiService, IHostedService
    {
        private readonly CtApi _ctApi;
        private readonly IConfiguration _config;

        public CtApiService(IConfiguration config)
        {
            _ctApi = new CtApi();
            _config = config;
        }

        #region IHostedService
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            SetCtApiDirectory(_config["CtApi:Path"]);

            var ip = _config["CtApi:Ip"];
            if (string.IsNullOrWhiteSpace(ip))
                await OpenAsync();
            else
                await OpenAsync(ip, _config["CtApi:User"], _config["CtApi:Password"]);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            //throw new NotImplementedException();
            _ctApi.Close();
            return Task.CompletedTask;
        }
        #endregion

        #region ICtApiService

        public void SetCtApiDirectory(string path)
        {
            _ctApi.SetCtApiDirectory(path);
        }

        public async Task OpenAsync(string ip = null, string user = null, string password = null)
        {
            if (string.IsNullOrEmpty(ip))
                await _ctApi.OpenAsync(null, null, null, CtOpen.Reconnect);
            else
                await _ctApi.OpenAsync(ip, user, password, 0);
        }

        // Чтение тега
        public async Task<string> TagReadAsync(string tagName)
        {
            return await _ctApi.TagReadAsync(tagName);
            //return (object)result;
        }

        // Запись тега
        public async Task<object> TagWriteAsync(string tagName, string value)
        {
            await _ctApi.TagWriteAsync(tagName, value); // _ctApi.TagWriteAsync возвращает Task
            return null; // Возвращаем null, потому что значение не возвращается
        }

        // Поиск тегов
        public async Task<IEnumerable<Dictionary<string, string>>> FindAsync(string tableName, string filter, string cluster, params string[] propertiesName)
        {
            return await _ctApi.FindAsync(tableName, filter, cluster, propertiesName);
        }

        public async Task<string> CicodeAsync(string cmd)
        {
            return await _ctApi.CicodeAsync(cmd);
        }

        public async Task<bool> IsConnected()
        {
            try
            {
                string value = await _ctApi.TagReadAsync("sWndTitle");
                return !string.IsNullOrEmpty(value);
            }
            catch
            {
                return false;
            }
        }

        public string UserInfo(int type)
        {
            return _ctApi.UserInfo(type);
        }

        #endregion

        #region Trend

        // Чтение тега
        public async Task<List<TrnData>> GetTrnData(string tagName, DateTime startTime, DateTime endTime)
        {
            //DateTime startTime = DateTime.UtcNow.AddMinutes(-60);
            //DateTime endTime = DateTime.UtcNow;
            float period = 1.0f;
            uint displayMode = DisplayMode.Get(Ordering.OldestToNewest, Condense.Mean, Stretch.Raw, 0, BadQuality.Zero, Raw.None);
            int dataMode = 1;

            var data = await _ctApi.TrnQueryAsync(startTime, endTime, period, tagName, displayMode, dataMode, "Cluster1");

            return data.ToList();
        }

        #endregion

    }
}
