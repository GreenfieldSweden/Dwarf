﻿using Dwarf.DataAccess;

namespace Dwarf.Interfaces
{
    internal interface IQueryConstructor
    {
        string Top(int i);
        string Limit(int offset, int rows);
        string Distinct { get; }
        string LeftContainer { get; }
        string RightContainer { get; }
        string TableNamePrefix { get; }
        string InnerJoin { get; }
        string LeftOuterJoin { get; }
        string Date(string columnName);
        string DatePart(DateParts datePart, string targetColumn);
    }
}