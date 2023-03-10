﻿// Copyright (c) 2022, Oracle and/or its affiliates.
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License, version 2.0, as
// published by the Free Software Foundation.
//
// This program is also distributed with certain software (including
// but not limited to OpenSSL) that is licensed under separate terms,
// as designated in a particular file or component or in included license
// documentation.  The authors of MySQL hereby grant you an
// additional permission to link the program and your derivative works
// with the separately licensed software that they have included with
// MySQL.
//
// Without limiting anything contained in the foregoing, this file,
// which is part of MySQL Connector/NET, is also subject to the
// Universal FOSS Exception, version 1.0, a copy of which can be found at
// http://oss.oracle.com/licenses/universal-foss-exception.
//
// This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License, version 2.0, for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software Foundation, Inc.,
// 51 Franklin St, Fifth Floor, Boston, MA 02110-1301  USA

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace MySql.EntityFrameworkCore.Migrations.Internal
{
  internal class MySqlMigrator : Migrator
  {
    private static readonly Dictionary<Type, Tuple<string, string>> _customMigrationCommands =
    new Dictionary<Type, Tuple<string, string>>
    {
    {
      typeof(DropPrimaryKeyOperation),
      new Tuple<string, string>(BeforeDropPrimaryKeyMigrationBegin, BeforeDropPrimaryKeyMigrationEnd)
    },
    {
      typeof(AddPrimaryKeyOperation),
      new Tuple<string, string>(AfterAddPrimaryKeyMigrationBegin, AfterAddPrimaryKeyMigrationEnd)
    },
    };

    private readonly IMigrationsAssembly _migrationsAssembly;
    private readonly IRawSqlCommandBuilder _rawSqlCommandBuilder;
    private readonly ICurrentDbContext _currentContext;
    private readonly IRelationalCommandDiagnosticsLogger _commandLogger;

    public MySqlMigrator(
      [NotNull] IMigrationsAssembly migrationsAssembly,
      [NotNull] IHistoryRepository historyRepository,
      [NotNull] IDatabaseCreator databaseCreator,
      [NotNull] IMigrationsSqlGenerator migrationsSqlGenerator,
      [NotNull] IRawSqlCommandBuilder rawSqlCommandBuilder,
      [NotNull] IMigrationCommandExecutor migrationCommandExecutor,
      [NotNull] IRelationalConnection connection,
      [NotNull] ISqlGenerationHelper sqlGenerationHelper,
      [NotNull] ICurrentDbContext currentContext,
      [NotNull] IModelRuntimeInitializer modelRuntimeInitializer,
      [NotNull] IDiagnosticsLogger<DbLoggerCategory.Migrations> logger,
      [NotNull] IRelationalCommandDiagnosticsLogger commandLogger,
      [NotNull] IDatabaseProvider databaseProvider)
      : base(
        migrationsAssembly,
        historyRepository,
        databaseCreator,
        migrationsSqlGenerator,
        rawSqlCommandBuilder,
        migrationCommandExecutor,
        connection,
        sqlGenerationHelper,
        currentContext,
        modelRuntimeInitializer,
        logger,
        commandLogger,
        databaseProvider)
    {
      _migrationsAssembly = migrationsAssembly;
      _rawSqlCommandBuilder = rawSqlCommandBuilder;
      _currentContext = currentContext;
      _commandLogger = commandLogger;
    }

    protected override IReadOnlyList<MigrationCommand> GenerateUpSql(
      Migration migration,
      MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
      var commands = base.GenerateUpSql(migration, options);

      return options.HasFlag(MigrationsSqlGenerationOptions.Script) &&
           options.HasFlag(MigrationsSqlGenerationOptions.Idempotent)
        ? commands
        : WrapWithCustomCommands(
            migration.UpOperations,
            commands.ToList(),
            options);
    }

    protected override IReadOnlyList<MigrationCommand> GenerateDownSql(
      Migration migration,
      Migration previousMigration,
      MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
      var commands = base.GenerateDownSql(migration, previousMigration, options);

      return options.HasFlag(MigrationsSqlGenerationOptions.Script) &&
           options.HasFlag(MigrationsSqlGenerationOptions.Idempotent)
        ? commands
        : WrapWithCustomCommands(
            migration.DownOperations,
            commands.ToList(),
            options);
    }

    public override string GenerateScript(
      string fromMigration = null,
      string toMigration = null,
      MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
      options |= MigrationsSqlGenerationOptions.Script;

      if (!options.HasFlag(MigrationsSqlGenerationOptions.Idempotent))
      {
        return base.GenerateScript(fromMigration, toMigration, options);
      }

      var operations = GetAllMigrationOperations(fromMigration, toMigration);

      var builder = new StringBuilder();

      builder.AppendJoin(string.Empty, GetMigrationCommandTexts(operations, true, options));
      builder.Append(base.GenerateScript(fromMigration, toMigration, options));
      builder.AppendJoin(string.Empty, GetMigrationCommandTexts(operations, false, options));

      return builder.ToString();
    }

    protected virtual List<MigrationOperation> GetAllMigrationOperations(string fromMigration, string toMigration)
    {
      IEnumerable<string> appliedMigrations;
      if (string.IsNullOrEmpty(fromMigration)
        || fromMigration == Migration.InitialDatabase)
      {
        appliedMigrations = Enumerable.Empty<string>();
      }
      else
      {
        var fromMigrationId = _migrationsAssembly.GetMigrationId(fromMigration);
        appliedMigrations = _migrationsAssembly.Migrations
          .Where(t => string.Compare(t.Key, fromMigrationId, StringComparison.OrdinalIgnoreCase) <= 0)
          .Select(t => t.Key);
      }

      PopulateMigrations(
        appliedMigrations,
        toMigration,
        out var migrationsToApply,
        out var migrationsToRevert,
        out var actualTargetMigration);

      return migrationsToApply
        .SelectMany(x => x.UpOperations)
        .Concat(migrationsToRevert.SelectMany(x => x.DownOperations))
        .ToList();
    }

    protected virtual IReadOnlyList<MigrationCommand> WrapWithCustomCommands(
      IReadOnlyList<MigrationOperation> migrationOperations,
      IReadOnlyList<MigrationCommand> migrationCommands,
      MigrationsSqlGenerationOptions options)
    {
      var beginCommandTexts = GetMigrationCommandTexts(migrationOperations, true, options);
      var endCommandTexts = GetMigrationCommandTexts(migrationOperations, false, options);

      return new List<MigrationCommand>(
        beginCommandTexts.Select(t => new MigrationCommand(_rawSqlCommandBuilder.Build(t),
        _currentContext.Context, _commandLogger))
        .Concat(migrationCommands)
        .Concat(endCommandTexts.Select(t => new MigrationCommand(_rawSqlCommandBuilder.Build(t), _currentContext.Context, _commandLogger)))
        );
    }

    protected virtual string[] GetMigrationCommandTexts(
    IReadOnlyList<MigrationOperation> migrationOperations,
    bool beginTexts,
    MigrationsSqlGenerationOptions options)
    => GetCustomCommands(migrationOperations)
      .Select(
        t => PrepareString(
          beginTexts
            ? t.Item1
            : t.Item2,
          options))
      .ToArray();

    protected virtual IReadOnlyList<Tuple<string, string>> GetCustomCommands(IReadOnlyList<MigrationOperation> migrationOperations)
      => _customMigrationCommands
        .Where(c => migrationOperations.Any(o => c.Key.IsInstanceOfType(o)) && c.Value != null)
        .Select(kvp => kvp.Value)
        .ToList();

    protected virtual string CleanUpScriptSpecificPseudoStatements(string commandText)
    {
      const string temporaryDelimiter = @"//";
      const string defaultDelimiter = @";";
      const string delimiterChangeRegexPatternFormatString = @"[\r\n]*[^\S\r\n]*DELIMITER[^\S\r\n]+{0}[^\S\r\n]*";
      const string delimiterUseRegexPatternFormatString = @"\s*{0}\s*$";

      var temporaryDelimiterRegexPattern = string.Format(
        delimiterChangeRegexPatternFormatString,
        $"(?:{Regex.Escape(temporaryDelimiter)}|{Regex.Escape(defaultDelimiter)})");

      var delimiter = Regex.Match(commandText, temporaryDelimiterRegexPattern, RegexOptions.IgnoreCase);
      if (delimiter.Success)
      {
        commandText = Regex.Replace(commandText, temporaryDelimiterRegexPattern, string.Empty, RegexOptions.IgnoreCase);

        commandText = Regex.Replace(
          commandText,
          string.Format(delimiterUseRegexPatternFormatString, temporaryDelimiter),
          defaultDelimiter,
          RegexOptions.IgnoreCase | RegexOptions.Multiline);
      }

      return commandText;
    }

    protected virtual string PrepareString(string str, MigrationsSqlGenerationOptions options)
    {
      str = options.HasFlag(MigrationsSqlGenerationOptions.Script)
        ? str
        : CleanUpScriptSpecificPseudoStatements(str);

      str = str
        .Replace("\r", string.Empty)
        .Replace("\n", Environment.NewLine);

      str += options.HasFlag(MigrationsSqlGenerationOptions.Script)
        ? Environment.NewLine + (
            options.HasFlag(MigrationsSqlGenerationOptions.Idempotent)
              ? Environment.NewLine
              : string.Empty)
        : string.Empty;

      return str;
    }

    #region Custom SQL

    private const string BeforeDropPrimaryKeyMigrationBegin = @"DROP PROCEDURE IF EXISTS `MYSQL_BEFORE_DROP_PRIMARY_KEY`;
    DELIMITER //
    CREATE PROCEDURE `MYSQL_BEFORE_DROP_PRIMARY_KEY`(IN `SCHEMA_NAME_ARGUMENT` VARCHAR(255), IN `TABLE_NAME_ARGUMENT` VARCHAR(255))
    BEGIN
    DECLARE HAS_AUTO_INCREMENT_ID TINYINT(1);
    DECLARE PRIMARY_KEY_COLUMN_NAME VARCHAR(255);
    DECLARE PRIMARY_KEY_TYPE VARCHAR(255);
    DECLARE SQL_EXP VARCHAR(1000);
    SELECT COUNT(*)
      INTO HAS_AUTO_INCREMENT_ID
      FROM `information_schema`.`COLUMNS`
      WHERE `TABLE_SCHEMA` = (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA()))
      AND `TABLE_NAME` = TABLE_NAME_ARGUMENT
      AND `Extra` = 'auto_increment'
      AND `COLUMN_KEY` = 'PRI'
      LIMIT 1;
    IF HAS_AUTO_INCREMENT_ID THEN
      SELECT `COLUMN_TYPE`
      INTO PRIMARY_KEY_TYPE
      FROM `information_schema`.`COLUMNS`
      WHERE `TABLE_SCHEMA` = (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA()))
        AND `TABLE_NAME` = TABLE_NAME_ARGUMENT
        AND `COLUMN_KEY` = 'PRI'
      LIMIT 1;
      SELECT `COLUMN_NAME`
      INTO PRIMARY_KEY_COLUMN_NAME
      FROM `information_schema`.`COLUMNS`
      WHERE `TABLE_SCHEMA` = (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA()))
        AND `TABLE_NAME` = TABLE_NAME_ARGUMENT
        AND `COLUMN_KEY` = 'PRI'
      LIMIT 1;
      SET SQL_EXP = CONCAT('ALTER TABLE `', (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA())), '`.`', TABLE_NAME_ARGUMENT, '` MODIFY COLUMN `', PRIMARY_KEY_COLUMN_NAME, '` ', PRIMARY_KEY_TYPE, ' NOT NULL;');
      SET @SQL_EXP = SQL_EXP;
      PREPARE SQL_EXP_EXECUTE FROM @SQL_EXP;
      EXECUTE SQL_EXP_EXECUTE;
      DEALLOCATE PREPARE SQL_EXP_EXECUTE;
    END IF;
    END //
    DELIMITER ;";

    private const string BeforeDropPrimaryKeyMigrationEnd = @"DROP PROCEDURE `MYSQL_BEFORE_DROP_PRIMARY_KEY`;";

    private const string AfterAddPrimaryKeyMigrationBegin = @"DROP PROCEDURE IF EXISTS `MYSQL_AFTER_ADD_PRIMARY_KEY`;
    DELIMITER //
    CREATE PROCEDURE `MYSQL_AFTER_ADD_PRIMARY_KEY`(IN `SCHEMA_NAME_ARGUMENT` VARCHAR(255), IN `TABLE_NAME_ARGUMENT` VARCHAR(255), IN `COLUMN_NAME_ARGUMENT` VARCHAR(255))
    BEGIN
    DECLARE HAS_AUTO_INCREMENT_ID INT(11);
    DECLARE PRIMARY_KEY_COLUMN_NAME VARCHAR(255);
    DECLARE PRIMARY_KEY_TYPE VARCHAR(255);
    DECLARE SQL_EXP VARCHAR(1000);
    SELECT COUNT(*)
      INTO HAS_AUTO_INCREMENT_ID
      FROM `information_schema`.`COLUMNS`
      WHERE `TABLE_SCHEMA` = (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA()))
      AND `TABLE_NAME` = TABLE_NAME_ARGUMENT
      AND `COLUMN_NAME` = COLUMN_NAME_ARGUMENT
      AND `COLUMN_TYPE` LIKE '%int%'
      AND `COLUMN_KEY` = 'PRI';
    IF HAS_AUTO_INCREMENT_ID THEN
      SELECT `COLUMN_TYPE`
      INTO PRIMARY_KEY_TYPE
      FROM `information_schema`.`COLUMNS`
      WHERE `TABLE_SCHEMA` = (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA()))
        AND `TABLE_NAME` = TABLE_NAME_ARGUMENT
        AND `COLUMN_NAME` = COLUMN_NAME_ARGUMENT
        AND `COLUMN_TYPE` LIKE '%int%'
        AND `COLUMN_KEY` = 'PRI';
      SELECT `COLUMN_NAME`
      INTO PRIMARY_KEY_COLUMN_NAME
      FROM `information_schema`.`COLUMNS`
      WHERE `TABLE_SCHEMA` = (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA()))
        AND `TABLE_NAME` = TABLE_NAME_ARGUMENT
        AND `COLUMN_NAME` = COLUMN_NAME_ARGUMENT
        AND `COLUMN_TYPE` LIKE '%int%'
        AND `COLUMN_KEY` = 'PRI';
      SET SQL_EXP = CONCAT('ALTER TABLE `', (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA())), '`.`', TABLE_NAME_ARGUMENT, '` MODIFY COLUMN `', PRIMARY_KEY_COLUMN_NAME, '` ', PRIMARY_KEY_TYPE, ' NOT NULL AUTO_INCREMENT;');
      SET @SQL_EXP = SQL_EXP;
      PREPARE SQL_EXP_EXECUTE FROM @SQL_EXP;
      EXECUTE SQL_EXP_EXECUTE;
      DEALLOCATE PREPARE SQL_EXP_EXECUTE;
    END IF;
    END //
    DELIMITER ;";

    private const string AfterAddPrimaryKeyMigrationEnd = @"DROP PROCEDURE `MYSQL_AFTER_ADD_PRIMARY_KEY`;";

    #endregion Custom SQL
  }
}
