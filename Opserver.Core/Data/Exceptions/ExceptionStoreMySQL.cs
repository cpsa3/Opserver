﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using StackExchange.Profiling;
using StackExchange.Exceptional;
using StackExchange.Opserver.Helpers;

namespace StackExchange.Opserver.Data.Exceptions
{
    /// <summary>
    /// mysql 
    /// </summary>
    public class ExceptionStore : PollNode
    {
        public const int PerAppSummaryCount = 1000;

        public override string ToString() => "Store: " + Settings.Name;

        private int? QueryTimeout => Settings.QueryTimeoutMs;
        public string Name => Settings.Name;
        public string Description => Settings.Description;
        public ExceptionsSettings.Store Settings { get; internal set; }

        public override int MinSecondsBetweenPolls => 1;
        public override string NodeType => "Exceptions";

        public override IEnumerable<Cache> DataPollers
        {
            get
            {
                yield return Applications;
                yield return ErrorSummary;
            }
        }

        protected override IEnumerable<MonitorStatus> GetMonitorStatus() { yield break; }
        protected override string GetMonitorStatusReason() { return null; }

        public ExceptionStore(ExceptionsSettings.Store settings) : base(settings.Name)
        {
            Settings = settings;
        }

        public Func<Cache<T>, Task> UpdateFromSql<T>(string opName, Func<Task<T>> getFromConnection) where T : class
        {
            return UpdateCacheItem("Exceptions Fetch: " + Name + ":" + opName,
                getFromConnection,
                addExceptionData: e => e.AddLoggedData("Server", Name));
        }

        private Cache<List<Application>> _applications;
        public Cache<List<Application>> Applications
        {
            get
            {
                return _applications ?? (_applications = new Cache<List<Application>>
                {
                    CacheForSeconds = Settings.PollIntervalSeconds,
                    UpdateCache = UpdateFromSql(nameof(Applications),
                            async () =>
                            {
                                var result = await QueryListAsync<Application>($"Applications Fetch: {Name}", @"Select ApplicationName as Name, 
       Sum(DuplicateCount) as ExceptionCount,
	   Sum(Case When CreationDate > date_add(now(), interval -@RecentSeconds second) Then DuplicateCount Else 0 End) as RecentExceptionCount,
	   MAX(CreationDate) as MostRecent
  From Exceptions
 Where DeletionDate Is Null
 Group By ApplicationName", new { Current.Settings.Exceptions.RecentSeconds }).ConfigureAwait(false);
                                result.ForEach(a => { a.StoreName = Name; a.Store = this; });
                                return result;
                            })
                });
            }
        }

        private Cache<List<Error>> _errorSummary;
        public Cache<List<Error>> ErrorSummary
        {
            get
            {
                return _errorSummary ?? (_errorSummary = new Cache<List<Error>>
                {
                    CacheForSeconds = Settings.PollIntervalSeconds,
                    UpdateCache = UpdateFromSql(nameof(ErrorSummary),
                        () => QueryListAsync<Error>($"ErrorSummary Fetch: {Name}", @"SELECT 
    e.Id,
    e.GUID,
    e.ApplicationName,
    e.MachineName,
    e.CreationDate,
    e.Type,
    e.IsProtected,
    e.Host,
    e.Url,
    e.HTTPMethod,
    e.IPAddress,
    e.Source,
    e.Message,
    e.StatusCode,
    e.ErrorHash,
    e.DuplicateCount
FROM
    (SELECT 
        e.id,
            (CASE e.applicationname
                WHEN curType(e.applicationname,0) THEN curRow(2)
                ELSE curRow(0)+1 AND curType(e.applicationname,1)
            END) + 1 AS r
    FROM
        Exceptions e, (SELECT curRow(0), curType(NULL,1)) r
    WHERE
        e.DeletionDate IS NULL
    ORDER BY e.applicationname , e.creationdate DESC) er
        INNER JOIN
    Exceptions e ON er.Id = e.Id
WHERE
    er.r <= @PerAppSummaryCount
ORDER BY CreationDate DESC", new { PerAppSummaryCount }))
                });
            }
        }

        public List<Error> GetErrorSummary(int maxPerApp, string appName = null)
        {
            var errors = ErrorSummary.Data;
            if (errors == null) return new List<Error>();
            // specific application
            if (appName.HasValue())
            {
                return errors.Where(e => e.ApplicationName == appName)
                             .Take(maxPerApp)
                             .ToList();
            }
            // all apps, 1000
            if (maxPerApp == PerAppSummaryCount)
            {
                return errors;
            }
            // app apps, n records
            return errors.GroupBy(e => e.ApplicationName)
                         .SelectMany(e => e.Take(maxPerApp))
                         .ToList();
        }

        /// <summary>
        /// Get all current errors, possibly per application
        /// </summary>
        /// <remarks>This does not populate Detail, it's comparatively large and unused in list views</remarks>
        public Task<List<Error>> GetAllErrorsAsync(int maxPerApp, string appName = null)
        {
            return QueryListAsync<Error>($"{nameof(GetAllErrorsAsync)}() for {Name} App: {appName ?? "All"}", @"SELECT 
    e.Id,
    e.GUID,
    e.ApplicationName,
    e.MachineName,
    e.CreationDate,
    e.Type,
    e.IsProtected,
    e.Host,
    e.Url,
    e.HTTPMethod,
    e.IPAddress,
    e.Source,
    e.Message,
    e.StatusCode,
    e.ErrorHash,
    e.DuplicateCount
FROM
    (SELECT 
        e.id,
            (CASE e.applicationname
                WHEN curType(e.applicationname,0) THEN curRow(2)
                ELSE curRow(0)+1 AND curType(e.applicationname,1)
            END) + 1 AS r
    FROM
        Exceptions e, (SELECT curRow(0), curType(NULL,1)) r
    WHERE
        e.DeletionDate IS NULL" + (appName.HasValue() ? " And ApplicationName = @appName" : "") + @"
    ORDER BY e.applicationname , e.creationdate DESC) er
        INNER JOIN
    Exceptions e ON er.Id = e.Id
WHERE
    er.r <= @maxPerApp
ORDER BY CreationDate DESC", new { maxPerApp, appName });
        }

        public Task<List<Error>> GetSimilarErrorsAsync(Error error, int max)
        {
            return QueryListAsync<Error>($"{nameof(GetSimilarErrorsAsync)}() for {Name}", @"    SELECT 
    e.Id,
    e.GUID,
    e.ApplicationName,
    e.MachineName,
    e.CreationDate,
    e.Type,
    e.IsProtected,
    e.Host,
    e.Url,
    e.HTTPMethod,
    e.IPAddress,
    e.Source,
    e.Message,
    e.Detail,
    e.StatusCode,
    e.ErrorHash,
    e.DuplicateCount,
    e.DeletionDate
FROM
    Exceptions e
WHERE
    ApplicationName = @ApplicationName
        AND Message = @Message
ORDER BY CreationDate DESC
LIMIT @max;", new { max, error.ApplicationName, error.Message });
        }

        public Task<List<Error>> GetSimilarErrorsInTimeAsync(Error error, int max)
        {
            return QueryListAsync<Error>($"{nameof(GetSimilarErrorsInTimeAsync)}() for {Name}", @"SELECT 
    e.Id,
    e.GUID,
    e.ApplicationName,
    e.MachineName,
    e.CreationDate,
    e.Type,
    e.IsProtected,
    e.Host,
    e.Url,
    e.HTTPMethod,
    e.IPAddress,
    e.Source,
    e.Message,
    e.Detail,
    e.StatusCode,
    e.ErrorHash,
    e.DuplicateCount,
    e.DeletionDate
FROM
    Exceptions e
WHERE
    CreationDate BETWEEN @start and @end
ORDER BY CreationDate DESC
LIMIT @max;", new { max, start = error.CreationDate.AddMinutes(-5), end = error.CreationDate.AddMinutes(5) });
        }

        public Task<List<Error>> FindErrorsAsync(string searchText, string appName, int max, bool includeDeleted)
        {
            return QueryListAsync<Error>($"{nameof(FindErrorsAsync)}() for {Name}", @"  SELECT 
    e.Id,
    e.GUID,
    e.ApplicationName,
    e.MachineName,
    e.CreationDate,
    e.Type,
    e.IsProtected,
    e.Host,
    e.Url,
    e.HTTPMethod,
    e.IPAddress,
    e.Source,
    e.Message,
    e.Detail,
    e.StatusCode,
    e.ErrorHash,
    e.DuplicateCount,
    e.DeletionDate
FROM
    Exceptions e
WHERE
    (Message LIKE @search OR Detail LIKE @search
        OR Url LIKE @search)" + (appName.HasValue() ? " And ApplicationName = @appName" : "") + (includeDeleted ? "" : " And DeletionDate Is Null") + @"
ORDER BY CreationDate DESC
LIMIT @max;", new { search = "%" + searchText + "%", appName, max });
        }

        public Task<int> DeleteAllErrorsAsync(string appName)
        {
            return ExecTaskAsync($"{nameof(DeleteAllErrorsAsync)}() (app: {appName}) for {Name}", @"
Update Exceptions 
   Set DeletionDate = UTC_TIMESTAMP() 
 Where DeletionDate Is Null 
   And IsProtected = 0 
   And ApplicationName = @appName", new { appName });
        }

        public Task<int> DeleteSimilarErrorsAsync(Error error)
        {
            return ExecTaskAsync($"{nameof(DeleteSimilarErrorsAsync)}('{error.GUID.ToString()}') (app: {error.ApplicationName}) for {Name}", @"
Update Exceptions 
   Set DeletionDate = UTC_TIMESTAMP() 
 Where ApplicationName = @ApplicationName
   And Message = @Message
   And DeletionDate Is Null
   And IsProtected = 0", new { error.ApplicationName, error.Message });
        }

        public Task<int> DeleteErrorsAsync(List<Guid> ids)
        {
            return ExecTaskAsync($"{nameof(DeleteErrorsAsync)}({ids.Count.ToString()} Guids) for {Name}", @"
Update Exceptions 
   Set DeletionDate = UTC_TIMESTAMP() 
 Where DeletionDate Is Null 
   And IsProtected = 0 
   And GUID In @ids", new { ids });
        }

        public async Task<Error> GetErrorAsync(Guid guid)
        {
            try
            {
                Error sqlError;
                using (MiniProfiler.Current.Step(nameof(GetErrorAsync) + "() (guid: " + guid.ToString() + ") for " + Name))
                using (var c = await GetConnectionAsync().ConfigureAwait(false))
                {
                    sqlError = await c.QueryFirstOrDefaultAsync<Error>(@"Select  * 
      From Exceptions 
     Where GUID = @guid
     limit 1", new { guid }, commandTimeout: QueryTimeout).ConfigureAwait(false);
                }
                if (sqlError == null) return null;

                // everything is in the JSON, but not the columns and we have to deserialize for collections anyway
                // so use that deserialized version and just get the properties that might change on the SQL side and apply them
                var result = Error.FromJson(sqlError.FullJson);
                result.DuplicateCount = sqlError.DuplicateCount;
                result.DeletionDate = sqlError.DeletionDate;
                result.ApplicationName = sqlError.ApplicationName;
                return result;
            }
            catch (Exception e)
            {
                Current.LogException(e);
                return null;
            }
        }

        public async Task<bool> ProtectErrorAsync(Guid guid)
        {
            return await ExecTaskAsync($"{nameof(ProtectErrorAsync)}() (guid: {guid.ToString()}) for {Name}", @"
Update Exceptions 
   Set IsProtected = 1, DeletionDate = Null
 Where GUID = @guid", new { guid }).ConfigureAwait(false) > 0;
        }

        public async Task<bool> DeleteErrorAsync(Guid guid)
        {
            return await ExecTaskAsync($"{nameof(DeleteErrorAsync)}() (guid: {guid.ToString()}) for {Name}", @"Update Exceptions 
   Set DeletionDate = UTC_TIMESTAMP()
 Where GUID = @guid 
   And DeletionDate Is Null", new { guid }).ConfigureAwait(false) > 0;
        }

        public async Task<List<T>> QueryListAsync<T>(string step, string sql, dynamic paramsObj)
        {
            try
            {
                using (MiniProfiler.Current.Step(step))
                using (var c = await GetConnectionAsync().ConfigureAwait(false))
                {
                    return await c.QueryAsync<T>(sql, paramsObj as object, commandTimeout: QueryTimeout).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Current.LogException(e);
                return new List<T>();
            }
        }

        public async Task<int> ExecTaskAsync(string step, string sql, dynamic paramsObj)
        {
            using (MiniProfiler.Current.Step(step))
            using (var c = await GetConnectionAsync().ConfigureAwait(false))
            {
                return await c.ExecuteAsync(sql, paramsObj as object, commandTimeout: QueryTimeout).ConfigureAwait(false);
            }
        }

        private Task<DbConnection> GetConnectionAsync() =>
            MySQLConnection.GetOpenAsync(Settings.ConnectionString, QueryTimeout);
    }
}