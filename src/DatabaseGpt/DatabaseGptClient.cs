﻿using System.Data;
using System.Text;
using ChatGptNet;
using DatabaseGpt.DataAccessLayer;
using DatabaseGpt.Exceptions;
using DatabaseGpt.Models;
using DatabaseGpt.Settings;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;

namespace DatabaseGpt;

internal class DatabaseGptClient : IDatabaseGptClient
{
    private readonly IChatGptClient chatGptClient;
    private readonly ISqlContext sqlContext;
    private readonly IServiceProvider serviceProvider;
    private readonly ResiliencePipeline pipeline;
    private readonly DatabaseSettings databaseSettings;

    public DatabaseGptClient(IChatGptClient chatGptClient, ISqlContext sqlContext, ResiliencePipelineProvider<string> pipelineProvider, IServiceProvider serviceProvider, IOptions<DatabaseSettings> databaseSettingsOptions)
    {
        this.chatGptClient = chatGptClient;
        this.sqlContext = sqlContext;
        this.serviceProvider = serviceProvider;
        databaseSettings = databaseSettingsOptions.Value;
        pipeline = pipelineProvider.GetPipeline(nameof(DatabaseGptClient));
    }

    public async Task<IDataReader> ExecuteNaturalLanguageQueryAsync(Guid sessionId, string question, NaturalLanguageQueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var conversationExists = await chatGptClient.ConversationExistsAsync(sessionId, cancellationToken);
        if (!conversationExists)
        {
            var tables = await GetTablesAsync(databaseSettings.ExcludedTables);

            var systemMessage = $"""
                You are an assistant that answers questions using the information stored in a SQL Server database.
                Your answers can only reference one or more of the following tables: '{string.Join(',', tables)}'.
                You can create only SELECT queries. Never create INSERT, UPDATE nor DELETE command.
                When you create a T-SQL query, consider the following information:
                {databaseSettings.SystemMessage}
                """;

            sessionId = await chatGptClient.SetupAsync(sessionId, systemMessage, cancellationToken);
        }

        var reader = await pipeline.ExecuteAsync(async cancellationToken =>
        {
            var request = $"""
                You must answer the following question, '{question}', using a T-SQL query. Take into account also the previous messages.
                From the comma separated list of tables available in the database, select those tables that might be useful in the generated T-SQL query.
                The selected tables should be returned in a comma separated list. Your response should just contain the comma separated list of selected tables.
                If there are no tables that might be useful, return only the string 'NONE', without any other words. You shouldn't never explain the reason why you haven't found any table.
                If the question is unclear or you don't understand the question, or you need a clarification, then return only the string 'NONE', without any other words. 
                """;

            if (options?.OnStarting is not null)
            {
                await options.OnStarting.Invoke(serviceProvider);
            }

            var response = await chatGptClient.AskAsync(sessionId, request, cancellationToken: cancellationToken);
            var candidateTables = response.GetContent()!;
            candidateTables = candidateTables.Trim('\'');

            if (candidateTables == "NONE")
            {
                throw new NoTableFoundException($"No available information in the provided tables can be useful for the question '{question}'.");
            }

            var tables = candidateTables.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (options?.OnCandidateTablesFound is not null)
            {
                await options.OnCandidateTablesFound.Invoke(new(sessionId, question, tables), serviceProvider);
            }

            var createTableScripts = await GetCreateTablesScriptAsync(tables, databaseSettings.ExcludedColumns);

            request = $"""
                A database contains the following tables and columns:
                {createTableScripts}
                Generate a T-SQL query to answer the question: '{question}' - the query must only reference table names and column names that appear in this request.
                For example, if the request contains the following CREATE TABLE statements:
                CREATE TABLE Table1 (Column1 VARCHAR(255), Column2 VARCHAR(255))
                CREATE TABLE Table2 (Column3 VARCHAR(255), Column4 VARCHAR(255))
                Then you should only reference Tables Table1 and Table2 and the query should only reference columns Column1, Column2, Column3 and Column4.
                Your response should just contain the T-SQL query, no other information is required. For example, never explain the meaning of the query nor explain how to use the query.
                You can create only SELECT queries. Never create INSERT, UPDATE nor DELETE commands.
                If the question of the user requires an INSERT, UPDATE or DELETE command, then return only the string 'NONE', without any other words. You shouldn't never explain the reason why you haven't created the query.
                """;

            response = await chatGptClient.AskAsync(sessionId, request, cancellationToken: cancellationToken);

            var sql = response.GetContent()!;

            if (sql == "NONE")
            {
                throw new InvalidSqlException($"The question '{question}' requires an INSERT, UPDATE or DELETE command, that isn't supported.");
            }

            sql = sql[sql.IndexOf("SELECT")..].Replace("```", string.Empty);

            if (options?.OnQueryGenerated is not null)
            {
                await options.OnQueryGenerated.Invoke(new(sessionId, question, tables, sql), serviceProvider);
            }

            var reader = await ExecuteQueryAsync(sql);
            return reader;
        }, cancellationToken);

        return reader;
    }

    private async Task<IEnumerable<string>> GetTablesAsync(IEnumerable<string> excludedTables)
    {
        var tables = await sqlContext.GetDataAsync<string>("SELECT TABLE_SCHEMA + '.' + TABLE_NAME AS Tables FROM INFORMATION_SCHEMA.TABLES;");
        return tables.Where(t => !excludedTables.Contains(t));
    }

    private async Task<string> GetCreateTablesScriptAsync(IEnumerable<string> tables, IEnumerable<string> excludedColumns)
    {
        var result = new StringBuilder();
        var splittedTableNames = tables.Select(t =>
        {
            var parts = t.Split('.');
            var schema = parts[0].Trim();
            var name = parts[1].Trim();
            return new { Schema = schema, Name = name };
        });

        foreach (var table in splittedTableNames)
        {
            var query = $"""
                SELECT STUFF(
                	(SELECT ',' + '[' + COLUMN_NAME + '] ' + 
                	    UPPER(DATA_TYPE) + ISNULL('(' + IIF(CHARACTER_MAXIMUM_LENGTH = -1, 'MAX', CAST(CHARACTER_MAXIMUM_LENGTH AS VARCHAR(10))) + ')','') + ' ' + 
                	    CASE WHEN IS_NULLABLE = 'YES' THEN 'NULL' ELSE 'NOT NULL' END
                	FROM
                        INFORMATION_SCHEMA.COLUMNS
                	WHERE
                        TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND COLUMN_NAME NOT IN @excludedColumns
                	FOR XML PATH('')), 1, 1, ''
                );
                """;

            var columns = await sqlContext.GetObjectAsync<string>(query, new { schema = table.Schema, table = table.Name, ExcludedColumns = excludedColumns });
            result.AppendLine($"CREATE TABLE [{table.Schema}].[{table.Name}] ({columns});");
        }

        return result.ToString();
    }

    private async Task<IDataReader> ExecuteQueryAsync(string sql)
    {
        var result = await sqlContext.GetDataReaderAsync(sql);
        return result;
    }
}