// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Framework.Extensions.ExceptionExtensions;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;
using Xunit.Sdk;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    [Collection("Database tests")] // Ensure all tests hitting the database are run sequentially (no parallel execution).
    public abstract class DatabaseTest : IDisposable
    {
        protected readonly ScoreStatisticsQueueProcessor Processor;

        protected CancellationToken CancellationToken => cancellationSource.Token;

        protected const int MAX_COMBO = 1337;

        protected const int TEST_BEATMAP_ID = 1;
        protected const int TEST_BEATMAP_SET_ID = 1;
        protected ushort TestBuildID;

        private readonly CancellationTokenSource cancellationSource;

        private Exception? firstError;

        protected DatabaseTest()
        {
            cancellationSource = Debugger.IsAttached
                ? new CancellationTokenSource()
                : new CancellationTokenSource(10000);

            Environment.SetEnvironmentVariable("REALTIME_DIFFICULTY", "0");

            Processor = new ScoreStatisticsQueueProcessor();
            Processor.Error += processorOnError;

            Processor.ClearQueue();

            using (var db = Processor.GetDatabaseConnection())
            {
                // just a safety measure for now to ensure we don't hit production. since i was running on production until now.
                // will throw if not on test database.
                if (db.QueryFirstOrDefault<int?>("SELECT * FROM osu_counts WHERE name = 'is_production'") != null)
                    throw new InvalidOperationException("You are trying to do something very silly.");

                db.Execute("TRUNCATE TABLE osu_user_stats");
                db.Execute("TRUNCATE TABLE osu_user_stats_mania");
                db.Execute("TRUNCATE TABLE osu_user_beatmap_playcount");
                db.Execute("TRUNCATE TABLE osu_user_month_playcount");
                db.Execute("TRUNCATE TABLE osu_beatmaps");
                db.Execute("TRUNCATE TABLE osu_beatmapsets");

                db.Execute("DELETE FROM scores");
                db.Execute("DELETE FROM score_process_history");

                // Temporary until osu-web images are updated.
                db.Execute("DROP VIEW IF EXISTS scores");
                db.Execute("DROP TABLE IF EXISTS solo_scores");
                db.Execute("CREATE TABLE `solo_scores` (\n  `id` bigint unsigned NOT NULL AUTO_INCREMENT,\n  `user_id` int unsigned NOT NULL,\n  `ruleset_id` smallint unsigned NOT NULL,\n  `beatmap_id` mediumint unsigned NOT NULL,\n  `has_replay` tinyint(1) NOT NULL DEFAULT '0',\n  `preserve` tinyint(1) NOT NULL DEFAULT '0',\n  `ranked` tinyint(1) NOT NULL DEFAULT '1',\n  `rank` char(2) NOT NULL DEFAULT '',\n  `passed` tinyint NOT NULL DEFAULT '0',\n  `accuracy` float unsigned NOT NULL DEFAULT '0',\n  `max_combo` int unsigned NOT NULL DEFAULT '0',\n  `total_score` int unsigned NOT NULL DEFAULT '0',\n  `data` json NOT NULL,\n  `pp` float unsigned DEFAULT NULL,\n  `legacy_score_id` bigint unsigned DEFAULT NULL,\n  `legacy_total_score` int unsigned DEFAULT NULL,\n  `started_at` timestamp NULL DEFAULT NULL,\n  `ended_at` timestamp NOT NULL,\n  `unix_updated_at` int unsigned NOT NULL DEFAULT (unix_timestamp()),\n  `build_id` smallint unsigned DEFAULT NULL,\n  PRIMARY KEY (`id`,`preserve`,`unix_updated_at`),\n  KEY `user_ruleset_index` (`user_id`,`ruleset_id`),\n  KEY `beatmap_user_index` (`beatmap_id`,`user_id`),\n  KEY `legacy_score_lookup` (`ruleset_id`,`legacy_score_id`)\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=COMPRESSED\n/*!50500 PARTITION BY RANGE  COLUMNS(`preserve`,unix_updated_at)\n(PARTITION p0catch VALUES LESS THAN (0,MAXVALUE) ENGINE = InnoDB,\n PARTITION p1 VALUES LESS THAN (MAXVALUE,MAXVALUE) ENGINE = InnoDB) */");
                db.Execute("CREATE VIEW scores AS SELECT * FROM solo_scores");

                db.Execute("TRUNCATE TABLE osu_builds");
                db.Execute("REPLACE INTO osu_counts (name, count) VALUES ('playcount', 0)");

                TestBuildID = db.QuerySingle<ushort>("INSERT INTO osu_builds (version, allow_performance) VALUES ('1.0.0', 1); SELECT LAST_INSERT_ID();");
            }

            Task.Run(() => Processor.Run(CancellationToken), CancellationToken);
        }

        protected ScoreItem SetScoreForBeatmap(uint beatmapId, Action<ScoreItem>? scoreSetup = null)
        {
            using (MySqlConnection conn = Processor.GetDatabaseConnection())
            {
                var score = CreateTestScore(beatmapId: beatmapId);

                scoreSetup?.Invoke(score);

                conn.Insert(score.Score);
                PushToQueueAndWaitForProcess(score);

                return score;
            }
        }

        private static ulong scoreIDSource;

        protected void PushToQueueAndWaitForProcess(ScoreItem item)
        {
            // To keep the flow of tests simple, require single-file addition of items.
            if (Processor.GetQueueSize() > 0)
                throw new InvalidOperationException("Queue was still processing an item when attempting to push another one.");

            long processedBefore = Processor.TotalProcessed;

            Processor.PushToQueue(item);

            WaitForDatabaseState($"SELECT score_id FROM score_process_history WHERE score_id = {item.Score.id}", item.Score.id, CancellationToken);
            WaitForTotalProcessed(processedBefore + 1, CancellationToken);
        }

        public static ScoreItem CreateTestScore(uint? rulesetId = null, uint? beatmapId = null)
        {
            var row = new SoloScore
            {
                id = Interlocked.Increment(ref scoreIDSource),
                user_id = 2,
                beatmap_id = beatmapId ?? TEST_BEATMAP_ID,
                ruleset_id = (ushort)(rulesetId ?? 0),
                started_at = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(180),
                ended_at = DateTimeOffset.UtcNow,
                max_combo = MAX_COMBO,
                total_score = 100000,
                rank = ScoreRank.S,
                passed = true
            };

            var scoreData = new SoloScoreData
            {
                Statistics =
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 0 }
                },
                MaximumStatistics =
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 2 }
                },
            };

            row.ScoreData = scoreData;

            return new ScoreItem(row);
        }

        protected void IgnoreProcessorExceptions()
        {
            Processor.Error -= processorOnError;
        }

        protected Beatmap AddBeatmap(Action<Beatmap>? beatmapSetup = null, Action<BeatmapSet>? beatmapSetSetup = null)
        {
            var beatmap = new Beatmap { approved = BeatmapOnlineStatus.Ranked };
            var beatmapSet = new BeatmapSet { approved = BeatmapOnlineStatus.Ranked };

            beatmapSetup?.Invoke(beatmap);
            beatmapSetSetup?.Invoke(beatmapSet);

            if (beatmap.beatmap_id == 0) beatmap.beatmap_id = TEST_BEATMAP_ID;
            if (beatmapSet.beatmapset_id == 0) beatmapSet.beatmapset_id = TEST_BEATMAP_SET_ID;

            if (beatmap.beatmapset_id > 0 && beatmap.beatmapset_id != beatmapSet.beatmapset_id)
                throw new ArgumentException($"{nameof(beatmapSetup)} method specified different {nameof(beatmap.beatmapset_id)} from the one specified in the {nameof(beatmapSetSetup)} method.");

            // Copy over set ID for cases where the setup steps only set it on the beatmapSet.
            beatmap.beatmapset_id = (uint)beatmapSet.beatmapset_id;

            using (var db = Processor.GetDatabaseConnection())
            {
                db.Insert(beatmap);
                db.Insert(beatmapSet);
            }

            return beatmap;
        }

        protected void AddBeatmapAttributes<TDifficultyAttributes>(uint? beatmapId = null, Action<TDifficultyAttributes>? setup = null)
            where TDifficultyAttributes : DifficultyAttributes, new()
        {
            var attribs = new TDifficultyAttributes
            {
                StarRating = 5,
                MaxCombo = 5,
            };

            setup?.Invoke(attribs);

            using (var db = Processor.GetDatabaseConnection())
            {
                foreach (var a in attribs.ToDatabaseAttributes())
                {
                    db.Insert(new BeatmapDifficultyAttribute
                    {
                        beatmap_id = beatmapId ?? TEST_BEATMAP_ID,
                        mode = 0,
                        mods = 0,
                        attrib_id = (ushort)a.attributeId,
                        value = Convert.ToSingle(a.value),
                    });
                }
            }
        }

        protected void WaitForTotalProcessed(long count, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Processor.TotalProcessed == count)
                    return;

                Thread.Sleep(50);
            }

            throw new XunitException("All scores were not successfully processed");
        }

        protected void WaitForDatabaseState<T>(string sql, T expected, CancellationToken cancellationToken, object? param = null)
        {
            using (var db = Processor.GetDatabaseConnection())
            {
                T? lastValue = default;

                while (true)
                {
                    if (!Debugger.IsAttached)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw new TimeoutException($"Waiting for database state took too long (expected: {expected} last: {lastValue} sql: {sql})");
                    }

                    lastValue = db.QueryFirstOrDefault<T>(sql, param);

                    if ((expected == null && lastValue == null) || expected?.Equals(lastValue) == true)
                        return;

                    firstError?.Rethrow();

                    Thread.Sleep(50);
                }
            }
        }

        private void processorOnError(Exception? exception, ScoreItem _) => firstError ??= exception;

#pragma warning disable CA1816
        public virtual void Dispose()
#pragma warning restore CA1816
        {
            cancellationSource.Cancel();
        }
    }
}
