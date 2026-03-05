using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TechEquipments
{
    /// <summary>
    /// Контролер DB-вкладок (Operation actions / Alarm history):
    /// - перевірка коннекту
    /// - debounce автоперезавантаження при зміні дати
    /// - cancel попереднього запиту при новому
    /// - gate (щоб не було паралельних DB-запитів)
    /// </summary>
    public sealed class DbController
    {
        private readonly IDbService _dbService;
        private readonly IDbHost _host;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private CancellationTokenSource? _cts;

        private readonly DispatcherTimer _reloadTimer;

        private readonly record struct DbQueryKey(DateTime Date, string Filter);

        private DbQueryKey? _lastOpActsQuery;
        private DbQueryKey? _lastAlarmQuery;

        public DbController(IDbService dbService, IDbHost host)
        {
            _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
            _host = host ?? throw new ArgumentNullException(nameof(host));

            // debounce-таймер на UI Dispatcher
            _reloadTimer = new DispatcherTimer(
                DispatcherPriority.Background,
                _host.Dispatcher
            )
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };

            _reloadTimer.Tick += async (_, __) =>
            {
                _reloadTimer.Stop();

                // тільки якщо DB вкладки і є коннект
                if (!_host.IsDbTabSelected || !_host.IsDbConnected)
                    return;

                try
                {
                    await LoadCurrentTabAsync(force: true);
                }
                catch (Exception ex)
                {
                    _host.BottomText = $"DB reload error: {ex.Message}";
                }
            };
        }

        /// <summary>
        /// Перевірка підключення до БД і оновлення IsDbConnected.
        /// </summary>
        public async Task CheckDbAsync()
        {
            bool ok;
            try
            {
                ok = await _dbService.CanConnectAsync();
            }
            catch
            {
                ok = false;
            }

            _host.SetDbConnected(ok);
        }

        /// <summary>
        /// Відміняє поточний DB-запит (якщо він виконується).
        /// Використовуй при зміні вкладки.
        /// </summary>
        public void CancelCurrentLoad()
        {
            try { _cts?.Cancel(); } catch { }
        }

        /// <summary>
        /// Debounce-планування перезавантаження DB при зміні дати/фільтра.
        /// </summary>
        public void ScheduleReload()
        {
            _reloadTimer.Stop();

            if (!_host.IsDbTabSelected)
                return;

            if (!_host.IsDbConnected)
                return;

            _reloadTimer.Start();
        }

        /// <summary>
        /// Завантажує дані для поточної DB-вкладки.
        /// Якщо force=false — не вантажимо, якщо запит (дата+фільтр) не змінився.
        /// </summary>
        public async Task LoadCurrentTabAsync(bool force)
        {
            if (!_host.IsDbConnected)
                return;

            if (!_host.IsDbTabSelected)
                return;

            var current = new DbQueryKey(_host.DbDate.Date, (_host.DbFilter ?? "").Trim());

            switch (_host.SelectedMainTab)
            {
                case MainTabKind.OperationActions:
                    if (!force && _lastOpActsQuery.HasValue && _lastOpActsQuery.Value.Equals(current))
                        return;

                    await LoadOperatorActsAsync(current);
                    _lastOpActsQuery = current;
                    break;

                case MainTabKind.AlarmHistory:
                    if (!force && _lastAlarmQuery.HasValue && _lastAlarmQuery.Value.Equals(current))
                        return;

                    await LoadAlarmHistoryAsync(current);
                    _lastAlarmQuery = current;
                    break;

                default:
                    return;
            }
        }

        /// <summary>
        /// DB: Operation actions.
        /// </summary>
        private async Task LoadOperatorActsAsync(DbQueryKey key)
        {
            await _gate.WaitAsync();
            CancellationTokenSource? myCts = null;

            try
            {
                _cts?.Cancel();
                _cts?.Dispose();

                myCts = new CancellationTokenSource();
                _cts = myCts;

                var ct = myCts.Token;

                _host.SetDbLoading(true);
                _host.BottomText = "Loading DB (Operator actions)...";

                var rows = await _dbService.GetOperatorActsAsync(key.Date, key.Filter, ct);

                await _host.Dispatcher.InvokeAsync(() =>
                {
                    _host.OperatorActRows.Clear();
                    foreach (var r in rows)
                        _host.OperatorActRows.Add(r);
                });

                _host.BottomText = $"DB Operator actions: {rows.Count}";
            }
            catch (OperationCanceledException)
            {
                _host.BottomText = "DB cancelled";
            }
            catch (Exception ex)
            {
                _host.BottomText = $"DB Error: {ex.Message}";
            }
            finally
            {
                _host.SetDbLoading(false);

                if (ReferenceEquals(_cts, myCts))
                {
                    _cts?.Dispose();
                    _cts = null;
                }

                _gate.Release();
            }
        }

        /// <summary>
        /// DB: Alarm history.
        /// </summary>
        private async Task LoadAlarmHistoryAsync(DbQueryKey key)
        {
            await _gate.WaitAsync();
            CancellationTokenSource? myCts = null;

            try
            {
                _cts?.Cancel();
                _cts?.Dispose();

                myCts = new CancellationTokenSource();
                _cts = myCts;

                var ct = myCts.Token;

                _host.SetDbLoading(true);
                _host.BottomText = "Loading DB (Alarm history)...";

                var rows = await _dbService.GetAlarmHistoryAsync(key.Date, key.Filter, ct);

                await _host.Dispatcher.InvokeAsync(() =>
                {
                    _host.AlarmHistoryRows.Clear();
                    foreach (var r in rows)
                        _host.AlarmHistoryRows.Add(r);
                });

                _host.BottomText = $"DB Alarm history: {rows.Count}";
            }
            catch (OperationCanceledException)
            {
                _host.BottomText = "DB cancelled";
            }
            catch (Exception ex)
            {
                _host.BottomText = $"DB Error: {ex.Message}";
            }
            finally
            {
                _host.SetDbLoading(false);

                if (ReferenceEquals(_cts, myCts))
                {
                    _cts?.Dispose();
                    _cts = null;
                }

                _gate.Release();
            }
        }
    }
}