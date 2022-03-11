﻿using EdgeDB;
using EdgeDB.DataTypes;
using System.Linq.Expressions;
using System;
using System.Reflection;
using System.Reflection.Emit;
using Test;

Logger.AddStream(Console.OpenStandardOutput(), StreamType.StandardOut);
Logger.AddStream(Console.OpenStandardError(), StreamType.StandardError);

var edgedb = new EdgeDBClient(EdgeDBConnection.FromProjectFile(@"../../../edgedb.toml"), new EdgeDBConfig
{
    Logger = Logger.GetLogger<EdgeDBClient>(),
});

var q = QueryBuilder.Select<PartialPerson>().Filter(x => EdgeQL.ILike(x.Name, "quin"));

var result = await edgedb.ExecuteAsync($"{q}", q.Arguments.ToDictionary(x => x.Key, x => x.Value));

var person = result.ResutAs<IReadOnlyCollection<PartialPerson>>();

await Task.Delay(-1);

[EdgeDBType("Person")]
public class PartialPerson
{
    [EdgeDBProperty("name")]
    public string? Name { get; set; }
}

// our model in a C# form
[EdgeDBType]
public class Person
{
    [EdgeDBProperty("name")]
    public string? Name { get; set; }

    [EdgeDBProperty("email")]
    public string? Email { get; set; }
}