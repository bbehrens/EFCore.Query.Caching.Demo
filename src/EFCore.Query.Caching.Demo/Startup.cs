﻿using EasyCaching.Core.Configurations;
using EasyCaching.Redis;
using EFCoreQueryCachingDemo.Database;
using EFCoreQueryCachingDemo.Extensions;
using EFCoreQueryCachingDemo.HttpClients;
using EFCoreQueryCachingDemo.Polly;
using EFCoreQueryCachingDemo.Services.Configuration;
using EFCoreQueryCachingDemo.Services.Helpers;
using EFCoreQueryCachingDemo.Services.Repositories;
using EFCoreQueryCachingDemo.Services;
using EFCoreSecondLevelCacheInterceptor;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace EFCoreQueryCachingDemo
{
	public class Startup
	{
		private const string ProviderName = "Redis";
		private const string SerializerName = "MessagePack";

		public Startup(IConfiguration configuration)
		{
			Configuration = configuration;
		}

		public IConfiguration Configuration { get; }

		public void ConfigureServices(IServiceCollection services)
		{
			services.AddOptions();

			// Register configuration
			services.AddSingleton(Configuration);

			RegisterConfigurations(services);

			services.AddHttpClient("PollyHttpClient").AddPolicyHandler(RetryPolicies.GetHttpClientRetryPolicy());

			// Read configurations
			var serviceConfig = Configuration.GetSection("ServiceConfig").Get<ServiceConfig>();
			var easyCachingConfig = Configuration.GetSection("EasyCachingConfig").Get<EasyCachingConfig>();

			// Register Multiplexer
			var multiplexer = RedisHelper.ConnectRedis(easyCachingConfig);
			services.AddSingleton<IConnectionMultiplexer>(multiplexer);

			// Register logger
			services.AddTransient(typeof(ILogger<>), typeof(Logger<>));

			// Register services
			services.AddTransient<ICityBikesNetworksRepository, CityBikesNetworksRepository>();
			services.AddTransient<ICityBikesService, CityBikesService>();
			services.AddTransient<ICityBikesHttpClient, CityBikesHttpClient>();
			services.AddTransient<IRedisCacheService, RedisCacheService>();
			services.AddScoped<IDataSeeder, DataSeeder>();

			services.AddCorsPolicy("EnableCORS");
			services.AddAndConfigureApiVersioning();
			services.AddHttpContextAccessor();
			services.AddEndpointsApiExplorer();

			// Add EFCoreSecondLevelCache Interceptor
			services.AddEFSecondLevelCache(options =>
			{
				// Use EasyCachingCoreProvider as cache provider
				options.UseEasyCachingCoreProvider(ProviderName, isHybridCache: false)
					.DisableLogging(true) // Set it to true for maximum performance
					.UseCacheKeyPrefix("EF_"); // Redis cache key prefix

				// Puts the whole system in cache. In this case calling the 'Cacheable()' methods won't be necessary.
				// If you specify the 'Cacheable()' method, its setting will override this global setting.
				// If you want to exclude some queries from this global cache, apply the 'NotCacheable()' method to them.
				// https://github.com/VahidN/EFCoreSecondLevelCacheInterceptor
				var timeOutMs = EnvironmentVariableProvider.GetSetting<int>(
					"EasyCachingConfig__ExpirationTimeoutMs", easyCachingConfig.ExpirationTimeoutMs);
				options.CacheAllQueries(CacheExpirationMode.Sliding, TimeSpan.FromMilliseconds(timeOutMs));
			});

			// More info: https://easycaching.readthedocs.io/en/latest/Redis/
			services.AddEasyCaching(options =>
			{
				// Uses the Redis cache provider
				options.UseRedis(config =>
				{
					config.DBConfig = new RedisDBOptions()
					{
						Password = EnvironmentVariableProvider.GetSetting<string>(
							"EasyCachingConfig__DbConfig_Password", easyCachingConfig.DbConfig_Password),
						IsSsl = EnvironmentVariableProvider.GetSetting<bool>("EasyCachingConfig__DbConfig_IsSsl",
							easyCachingConfig.DbConfig_IsSsl),
						SslHost = EnvironmentVariableProvider.GetSetting<string>("EasyCachingConfig__DbConfig_SslHost",
							easyCachingConfig.DbConfig_SslHost),
						ConnectionTimeout = EnvironmentVariableProvider.GetSetting<int>(
							"EasyCachingConfig__ConnectionTimeout", easyCachingConfig.DbConfig_ConnectionTimeout),
						AllowAdmin = EnvironmentVariableProvider.GetSetting<bool>(
							"EasyCachingConfig__DbConfig_AllowAdmin", easyCachingConfig.DbConfig_AllowAdmin),
						Database = 0
					};
					config.DBConfig.Endpoints.Add(new ServerEndPoint(
						EnvironmentVariableProvider.GetSetting<string>("EasyCachingConfig__DbConfig_Endpoint",
							easyCachingConfig.DbConfig_Endpoint),
						EnvironmentVariableProvider.GetSetting<int>("EasyCachingConfig__DbConfig_Port",
							easyCachingConfig.DbConfig_Port)));
					config.MaxRdSecond = EnvironmentVariableProvider.GetSetting<int>("EasyCachingConfig__MaxRdSecond",
						easyCachingConfig.MaxRdSecond);
					config.EnableLogging =
						EnvironmentVariableProvider.GetSetting<bool>("EasyCachingConfig__EnableLogging",
							easyCachingConfig.EnableLogging);
					config.LockMs =
						EnvironmentVariableProvider.GetSetting<int>("EasyCachingConfig__LockMs",
							easyCachingConfig.LockMs);
					config.SleepMs =
						EnvironmentVariableProvider.GetSetting<int>("EasyCachingConfig__SleepMs",
							easyCachingConfig.SleepMs);
					config.CacheNulls = EnvironmentVariableProvider.GetSetting<bool>(
						"EasyCachingConfig__CacheNulls", easyCachingConfig.CacheNulls);
					config.SerializerName = SerializerName;
				}, ProviderName)
				.WithMessagePack(opt =>
				{
					opt.EnableCustomResolver = true;
					opt.CustomResolvers = CompositeResolver.Create(
						new IMessagePackFormatter[]
						{
							DbNullFormatter.Instance
						},
						new IFormatterResolver[]
						{
							NativeDateTimeResolver.Instance,
							ContractlessStandardResolver.Instance,
							StandardResolverAllowPrivate.Instance
						});
				}, SerializerName);
			});

			var mySqlConnectionString = EnvironmentVariableProvider.GetSetting<string>("ServiceConfig__MySqlConnectionString", serviceConfig.MySqlConnectionString);

			// Registers the given database context as a service
			services.AddDbContextPool<DataContext>((provider, options) =>
			{
				options.UseMySql(mySqlConnectionString, ServerVersion.AutoDetect(mySqlConnectionString), opt =>
				{
					opt.CommandTimeout((int)TimeSpan.FromSeconds(60).TotalSeconds);
					opt.EnableRetryOnFailure(5, TimeSpan.FromSeconds(30), null);
				});
				// Add second level cache interceptor
				options.AddInterceptors(provider.GetRequiredService<SecondLevelCacheInterceptor>());
			});

			services.AddControllers()
				.ConfigureApiBehaviorOptions(options =>
				{
					options.SuppressConsumesConstraintForFormFileParameters = true;
					options.SuppressInferBindingSourcesForParameters = true;
					options.SuppressModelStateInvalidFilter = true;
					options.SuppressMapClientErrors = true;
					options.ClientErrorMapping[404].Link = "https://httpstatuses.com/404";
				})
				.AddNewtonsoftJson(options =>
				{
					options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
				});

			services.AddRouting(options => options.LowercaseUrls = true);
			services.AddSwaggerMiddleware();
		}

		public async void Configure(IApplicationBuilder app, IConfiguration config, IServiceCollection services)
		{
			app.UseHsts();
			app.UseStaticFiles();
			app.UseRouting();
			app.UseCors("EnableCORS");
			app.UseApiExceptionHandling();
			app.UseSwaggerMiddleware(config);

			app.UseEndpoints(configure =>
			{
				configure.MapControllers();
				// Redirect root to Swagger UI
				configure.MapGet("", context =>
				{
					context.Response.Redirect("./swagger/index.html", permanent: false);
					return Task.FromResult(0);
				});
				configure.MapDefaultControllerRoute();
			});

			// Seed data
			using IServiceScope scope = services.BuildServiceProvider().CreateScope();
			var dataSeeder = scope.ServiceProvider.GetRequiredService<IDataSeeder>();
			await dataSeeder.InitializeAsync().ConfigureAwait(false);
		}

		/// <summary>
		/// Register a configuration instances which TOptions will bind against
		/// </summary>
		/// <param name="services"></param>
		protected void RegisterConfigurations(IServiceCollection services)
		{
			services.Configure<ServiceConfig>(Configuration.GetSection(nameof(ServiceConfig)));
			services.Configure<SwaggerConfig>(Configuration.GetSection(nameof(SwaggerConfig)));
			services.Configure<EasyCachingConfig>(Configuration.GetSection(nameof(EasyCachingConfig)));
		}
	}
}
