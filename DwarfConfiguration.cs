﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Evergreen.Dwarf.Attributes;
using Evergreen.Dwarf.DataAccess.SqlCe;
using Evergreen.Dwarf.Extensions;
using Evergreen.Dwarf.DataAccess;
using Evergreen.Dwarf.Interfaces;
using Evergreen.Dwarf.Proxy;
using Evergreen.Dwarf.Utilities;

namespace Evergreen.Dwarf
{
    /// <summary>
    /// Configuration object for the Dwarf framework
    /// </summary>
    public class DwarfConfiguration<T> : IDwarfConfiguration
    {
        #region Properties

        #region ConnectionString

        /// <summary>
        /// The connection string
        /// </summary>
        public string ConnectionString { get; set; }

        #endregion ConnectionString

        #region UseDefaultAuditLogService

        /// <summary>
        /// Gets or Sets if Dwarf's default AuditLog Service should be used
        /// </summary>
        public bool UseDefaultAuditLogService { get; set; }

        #endregion UseDefaultAuditLogService

        #region UseDefaultErrorLogService

        /// <summary>
        /// Gets or Sets if Dwarf's default ErrorLog Service should be used
        /// </summary>
        public bool UseDefaultErrorLogService { get; set; }

        #endregion UseDefaultErrorLogService

        #region ErrorLogService

        /// <summary>
        /// The ErrorLog Service implementation
        /// </summary>
        public IErrorLogService ErrorLogService { get; set; }

        #endregion ErrorLogService

        #region AuditLogService

        /// <summary>
        /// The AuditLogService Service implementation
        /// </summary>
        public IAuditLogService AuditLogService { get; set; }

        #endregion AuditLogService

        #region UserService

        /// <summary>
        /// The User Service implementation
        /// </summary>
        public IUserService UserService { get; set; }

        #endregion UserService

        #region DatabaseType

        /// <summary>
        /// Sets the underlying database vendor
        /// </summary>
        public DatabaseTypes? DatabaseType { private get; set; }

        #endregion DatabaseType

        #region DatabaseScripts

        private IDatabaseScripts scripts;

        /// <summary>
        /// Gets the object for database scripts
        /// </summary>
        public IDatabaseScripts DatabaseScripts
        {
            get { return scripts; }
        }

        #endregion DatabaseScripts

        #region Database

        private DatabaseOperator<T> database;

        /// <summary>
        /// Object for performing various database operations
        /// </summary>
        public IDatabaseOperator Database
        {
            get { return database ?? (database = new DatabaseOperator<T>()); }
        }

        #endregion Database

        #endregion Properties

        #region Methods

        #region SuspendAuditLogging

        /// <summary>
        /// Suspends audit logging for the current request
        /// </summary>
        public void SuspendAuditLogging()
        {
            DwarfContext<T>.GetDBContext().IsAuditLoggingSuspended = true;
        }

        #endregion SuspendAuditLogging

        #region ResumeAuditLogging

        /// <summary>
        /// Resumes audit logging for the current request
        /// </summary>
        public void ResumeAuditLogging()
        {
            DwarfContext<T>.GetDBContext().IsAuditLoggingSuspended = false;
        }

        #endregion ResumeAuditLogging

        #region Configure

        /// <summary>
        /// Applies the defined configuration
        /// </summary>
        public IDwarfConfiguration Configure()
        {
            if (DwarfContext<T>.IsConfigured())
                return this;

            var assembly = typeof (T).Assembly;

            switch (DatabaseType)
            {
                case DatabaseTypes.SqlServer:
                {
                    Cfg.Databases[assembly] = new SqlServerDatabase();
                    Cfg.QueryConstructors[assembly] = new SqlServerQueryConstructor();
                    scripts = new SqlServerDatabaseScripts<T>();
                    break;
                }
                case DatabaseTypes.SqlCe:
                {
                    Cfg.Databases[assembly] = new SqlCeDatabase();
                    Cfg.QueryConstructors[assembly] = new SqlCeQueryConstructor();
                    scripts = new SqlCeDatabaseScripts<T>();
                    break;
                }
                default:
                {
                    Cfg.Databases[assembly] = new SqlServerDatabase();
                    Cfg.QueryConstructors[assembly] = new SqlServerQueryConstructor();
                    scripts = new SqlServerDatabaseScripts<T>();
                    break;
                }
            }

            Cfg.ConnectionString[assembly] = Cfg.Databases[assembly].CreateConnectionString<T>(ConnectionString).ToString();

            if (UseDefaultAuditLogService)
                AuditLogService = new AuditLogService<T>();

            if (UseDefaultErrorLogService)
                ErrorLogService = new ErrorLogService<T>();

            ConfigureServices();
            DwarfContext<T>.AddConfig(this);

            InitializeExpressionTrees();
            
            return this;
        }

        #endregion Configure

        #region ConfigureServices

        /// <summary>
        /// Setup fake services if 
        /// </summary>
        private void ConfigureServices()
        {
            if (ErrorLogService == null)
                ErrorLogService = new FakeErrorLogService();

            if (UserService == null)
                UserService = new FakeUserService();
        }

        #endregion ConfigureServices

        #region InitializeExpressionTrees

        private static void InitializeExpressionTrees()
        {
            foreach (var validType in DwarfHelper.GetValidTypes<T>())
            {
                Cfg.PropertyExpressions[validType] = new Dictionary<string, ExpressionProperty>();

                var statics = validType.GetProperties(BindingFlags.Public | BindingFlags.Static);
                var props = validType.GetProperties().Where(x => !statics.Contains(x)).ToList();

                if (validType.Implements<ICompositeId>())
                    props.Remove(props.FirstOrDefault(x => x.Name.Equals("Id")));

                foreach (var propertyInfo in props)
                    Cfg.PropertyExpressions[validType][propertyInfo.Name] = new ExpressionProperty(propertyInfo);

                Cfg.ProjectionProperties[validType] = DwarfHelper.GetProjectionProperties<T>(validType).ToList();
                Cfg.DBProperties[validType] = InitDBProperties(validType).ToList();
                Cfg.PKProperties[validType] = InitPKProperties(validType).ToList();
                Cfg.FKProperties[validType] = InitFKProperties(validType).ToList();
                Cfg.OneToManyProperties[validType] = (from ep in PropertyHelper.GetProperties(validType)
                                                     from attribute in ep.GetCustomAttributes(false)
                                                     where attribute is OneToManyAttribute
                                                     select ep).ToList();

                Cfg.ManyToManyProperties[validType] = (from ep in PropertyHelper.GetProperties(validType)
                                                      from attribute in ep.GetCustomAttributes(false)
                                                      where attribute is ManyToManyAttribute
                                                      select ep).ToList();

                Cfg.GemListProperties[validType] = (from ep in PropertyHelper.GetProperties(validType)
                                                      where ep.PropertyType.Implements<IGemList>()
                                                      select ep).ToList();

                var fkPropertiesToOverride = new List<ExpressionProperty>();

                foreach (var source in DwarfHelper.GetFKProperties<T>(validType).Where(x => !DwarfPropertyAttribute.GetAttribute(x.ContainedProperty).EagerLoad))
                {
                    if (!source.ContainedProperty.GetGetMethod().IsVirtual)
                        throw new InvalidOperationException("The property \"" + source.Name + "\" in \"" + validType.Name + "\" must be virtual the enable Lazy Loading. Otherwise set EagerLoad to True");

                    fkPropertiesToOverride.Add(source);
                }

                if (!Cfg.DBProperties[validType].Any() && !Cfg.PKProperties[validType].Any())
                    throw new InvalidOperationException(validType.Name + " will result in a columnless table. That will not fly... ");

                ProxyGenerator.Create(validType, fkPropertiesToOverride);

                var pi = DwarfHelper.GetOrderByProperty(validType);

                if (pi != null)
                    Cfg.OrderBySql[validType] = "[" + validType.Name + "].[" + QueryBuilder.GetColumnName(pi.ContainedProperty) + "] " + DwarfHelper.GetOrderByDirection(validType);
                else
                    Cfg.OrderBySql[validType] = String.Empty;

                if (validType.Implements<IErrorLog>() || validType.Implements<IAuditLog>())
                    continue;
                
                var value = Expression.Parameter(typeof(Guid), "value");

                var mi = validType.FindMethodRecursively("Load", (BindingFlags.Static | BindingFlags.Public), new[] { typeof(Guid) });
                Cfg.LoadExpressions[validType] = Expression.Lambda<Func<Guid, object>>(Expression.Call(mi, value), new[] { value }).Compile();
            }

            foreach (var validType in typeof(T).Assembly.GetTypes().Where(type => type.Implements<IGem>() && !type.IsAbstract))
            {
                var value = Expression.Parameter(typeof(string), "value");

                var mi = validType.FindMethodRecursively("Load", (BindingFlags.Static | BindingFlags.Public), new[] { typeof(string) });
                Cfg.LoadGem[validType] = Expression.Lambda<Func<string, object>>(Expression.Call(mi, value), new[] { value }).Compile();
            }
        }

        #endregion InitializeExpressionTrees

        #region InitDBProperties

        internal static IEnumerable<ExpressionProperty> InitDBProperties(Type type)
        {
            if (!type.Implements<ICompositeId>())
                yield return PropertyHelper.GetProperty(type, "Id");

            foreach (var ep in PropertyHelper.GetProperties(type))
            {
                if (ep.Name.Equals("Id"))
                    continue;

                foreach (var attribute in ep.GetCustomAttributes(false))
                {
                    if (attribute is DwarfPropertyAttribute)
                        yield return ep;
                }
            }
        }

        #endregion InitDBProperties

        #region InitPKProperties

        internal static IEnumerable<ExpressionProperty> InitPKProperties(Type type)
        {
            if (!type.Implements<ICompositeId>())
                yield return PropertyHelper.GetProperty(type, "Id");
            else
            {
                foreach (var ep in PropertyHelper.GetProperties(type))
                {
                    if (ep.Name.Equals("Id"))
                        continue;

                    foreach (var attribute in ep.GetCustomAttributes(false))
                    {
                        if (attribute is DwarfPropertyAttribute && ((DwarfPropertyAttribute)attribute).IsPrimaryKey)
                            yield return ep;
                    }
                }
            }
        }

        #endregion InitPKProperties        
        
        #region InitFKProperties

        internal static IEnumerable<ExpressionProperty> InitFKProperties(Type type)
        {
            return from ep in PropertyHelper.GetProperties(type)
                   from attribute in ep.GetCustomAttributes(false)
                   where attribute is DwarfPropertyAttribute && DwarfPropertyAttribute.IsFK(ep)
                   select ep;
        }

        #endregion InitFKProperties

        #region FakeErrorLogService

        internal class FakeErrorLogService : IErrorLogService
        {
            public IErrorLog Logg(Exception exception, bool suppressMessage = false)
            {
                return CreateInstance();
            }

            public IErrorLog CreateInstance()
            {
                return default(IErrorLog);
            }

            public IErrorLog Load(Guid id)
            {
                return default(IErrorLog);
            }

            public List<IErrorLog> LoadAll()
            {
                return new List<IErrorLog>();
            }
        }

        #endregion FakeErrorLogService

        #region FakeUserService

        internal class FakeUserService : IUserService
        {
            private static readonly FakeUser fakeUser = new FakeUser();

            public IUser CurrentUser
            {
                get { return fakeUser; }
                set { }
            }
        }

        internal class FakeUser : IUser
        {
            public string UserName
            {
                get { return ""; }
                set { throw new NotImplementedException(); }
            }
        }

        #endregion FakeUserService

        #region GetDwarfTypes

        /// <summary>
        /// The bound model's IDwarfs
        /// </summary>
        public IEnumerable<Type> GetDwarfTypes()
        {
            return DwarfHelper.GetValidTypes<T>();
        }

        #endregion GetDwarfTypes

        #region GetGemTypes

        /// <summary>
        /// The bound model's public Gems
        /// </summary>
        public IEnumerable<Type> GetGemTypes()
        {
            return typeof(T).Assembly.GetTypes().Where(type => type.Implements<IGem>() && !type.IsAbstract).ToList();
        }

        #endregion GetGemTypes

        #endregion Methods
    }
}