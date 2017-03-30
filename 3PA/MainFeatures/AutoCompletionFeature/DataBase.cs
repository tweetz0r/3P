﻿#region header

// ========================================================================
// Copyright (c) 2017 - Julien Caillon (julien.caillon@gmail.com)
// This file (DataBase.cs) is part of 3P.
// 
// 3P is a free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// 3P is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with 3P. If not, see <http://www.gnu.org/licenses/>.
// ========================================================================

#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using _3PA.Lib;
using _3PA.MainFeatures.Parser;
using _3PA.MainFeatures.Pro;

namespace _3PA.MainFeatures.AutoCompletionFeature {
    internal class DataBase {
        #region Singleton

        private static DataBase _instance;

        public static DataBase Instance {
            get { return _instance ?? (_instance = new DataBase()); }
        }

        #endregion

        #region events

        /// <summary>
        /// Event published when the current database information is updated
        /// </summary>
        public event Action OnDatabaseUpdate;

        #endregion

        public DataBase() {
            UpdateDatabaseInfo();
        }

        #region fields

        /// <summary>
        /// List of Databases (each of which contains list of tables > list of fields/indexes/triggers)
        /// </summary>
        private List<ParsedDataBase> _dataBases = new List<ParsedDataBase>();

        /// <summary>
        /// List of sequences of the database
        /// </summary>
        private List<ParsedSequence> _sequences = new List<ParsedSequence>();

        private List<CompletionItem> _dbItems;

        private bool _isExtracting;

        /// <summary>
        /// Action called when an extraction is done
        /// </summary>
        private Action _onExtractionDone;

        #endregion

        #region public methods

        /// <summary>
        /// returns the path of the current dump file
        /// </summary>
        /// <returns></returns>
        public string GetCurrentDumpPath {
            get { return Path.Combine(Config.FolderDatabase, GetOutputName); }
        }

        /// <summary>
        /// returns true if the database info is available
        /// </summary>
        /// <returns></returns>
        public bool IsDbInfoAvailable {
            get { return File.Exists(GetCurrentDumpPath); }
        }

        /// <summary>
        /// Tries to load the database information of the current ProgressEnv, 
        /// returns false the info is not available
        /// </summary>
        /// <returns></returns>
        public void UpdateDatabaseInfo() {
            _dbItems = null;
            if (IsDbInfoAvailable) {
                // read file, extract info
                Read(GetCurrentDumpPath);
            } else {
                // reset
                _dataBases.Clear();
                _sequences.Clear();
            }

            if (OnDatabaseUpdate != null)
                OnDatabaseUpdate();
        }

        /// <summary>
        /// Deletes the file corresponding to the current database (if it exists)
        /// </summary>
        public void DeleteCurrentDbInfo() {
            if (!Utils.DeleteFile(GetCurrentDumpPath))
                UserCommunication.Notify("Couldn't delete the current database info stored in the file :<br>" + GetCurrentDumpPath.ToHtmlLink(), MessageImg.MsgError, "Delete failed", "Current database info");
            UpdateDatabaseInfo();
        }

        /// <summary>
        /// Should be called to extract the database info from the current environnement
        /// </summary>
        public void FetchCurrentDbInfo(Action onExtractionDone) {
            try {
                // dont extract 2 db at once
                if (_isExtracting) {
                    UserCommunication.Notify("Already fetching info for another environment, please wait the end of the previous execution!", MessageImg.MsgWarning, "Database info", "Extracting database structure", 5);
                    return;
                }

                // save the filename of the output database info file for this environment
                UserCommunication.Notify("Now fetching info on all the connected databases for the current environment<br>You will be warned when the process is over", MessageImg.MsgInfo, "Database info", "Extracting database structure", 5);

                var exec = new ProExecution {
                    OnExecutionEnd = execution => _isExtracting = false,
                    OnExecutionOk = ExtractionDoneOk,
                    NeedDatabaseConnection = true,
                    ExtractDbOutputPath = GetOutputName
                };
                _onExtractionDone = onExtractionDone;
                _isExtracting = exec.Do(ExecutionType.Database);
            } catch (Exception e) {
                ErrorHandler.ShowErrors(e, "FetchCurrentDbInfo");
            }
        }

        /// <summary>
        /// Method called after the execution of the program extracting the db info
        /// </summary>
        private void ExtractionDoneOk(ProExecution lastExec) {
            // copy the dump to the folder database
            if (Utils.CopyFile(lastExec.ExtractDbOutputPath, Path.Combine(Config.FolderDatabase, Path.GetFileName(lastExec.ExtractDbOutputPath) ?? ""))) {
                // update info
                UpdateDatabaseInfo();
                UserCommunication.Notify("Database structure extracted with success!<br>The autocompletion has been updated with the latest info, enjoy!", MessageImg.MsgOk, "Database info", "Extracting database structure", 10);
                if (_onExtractionDone != null) {
                    _onExtractionDone();
                    _onExtractionDone = null;
                }
            }
        }

        #endregion

        #region private methods

        /// <summary>
        /// Returns the output file name for the current appli/env
        /// </summary>
        /// <returns></returns>
        private string GetOutputName {
            get { return (Config.Instance.EnvName + "_" + Config.Instance.EnvSuffix + "_" + Config.Instance.EnvDatabase).ToValidFileName().ToLower() + ".dump"; }
        }

        /// <summary>
        /// This method parses the output of the .p procedure that exports the database info
        /// and fills _dataBases
        /// It then updates the parser with the new info
        /// </summary>
        private void Read(string filePath) {
            if (!File.Exists(filePath)) return;
            _dataBases.Clear();
            _sequences.Clear();

            var defaultToken = new TokenEos(null, 0, 0, 0, 0);
            ParsedDataBase currentDb = null;
            ParsedTable currentTable = null;

            Utils.ForEachLine(filePath, null, (i, items) => {
                var splitted = items.Split('\t');
                switch (items[0]) {
                    case 'H':
                        // base
                        //#H|<Dump date ISO 8601>|<Dump time>|<Logical DB name>|<Physical DB name>|<Progress version>
                        if (splitted.Length != 6)
                            return;
                        currentDb = new ParsedDataBase(
                            splitted[3],
                            splitted[4],
                            splitted[5],
                            new List<ParsedTable>());
                        _dataBases.Add(currentDb);
                        break;
                    case 'S':
                        if (splitted.Length != 3 || currentDb == null)
                            return;
                        _sequences.Add(new ParsedSequence {
                            SeqName = splitted[1],
                            DbName = currentDb.Name
                        });
                        break;
                    case 'T':
                        // table
                        //#T|<Table name>|<Table ID>|<Table CRC>|<Dump name>|<Description>
                        if (splitted.Length != 6 || currentDb == null)
                            return;
                        currentTable = new ParsedTable(
                            splitted[1],
                            defaultToken,
                            splitted[2],
                            splitted[3],
                            splitted[4],
                            splitted[5],
                            "",
                            false,
                            new List<ParsedField>(),
                            new List<ParsedIndex>(),
                            new List<ParsedTrigger>(),
                            "");
                        currentDb.Tables.Add(currentTable);
                        break;
                    case 'X':
                        // trigger
                        //#X|<Parent table>|<Event>|<Proc name>|<Trigger CRC>
                        if (splitted.Length != 5 || currentTable == null)
                            return;
                        currentTable.Triggers.Add(new ParsedTrigger(
                            splitted[2],
                            splitted[3]));
                        break;
                    case 'I':
                        // index
                        //#I|<Parent table>|<Index name>|<Primary? 0/1>|<Unique? 0/1>|<Index CRC>|<Fileds separated with %>
                        if (splitted.Length != 7 || currentTable == null)
                            return;
                        var flag = splitted[3].Equals("1") ? ParsedIndexFlag.Primary : ParsedIndexFlag.None;
                        if (splitted[4].Equals("1")) flag = flag | ParsedIndexFlag.Unique;
                        currentTable.Indexes.Add(new ParsedIndex(
                            splitted[2],
                            flag,
                            splitted[6].Split('%').ToList()));
                        break;
                    case 'F':
                        // field
                        //#F|<Parent table>|<Field name>|<Type>|<Format>|<Order #>|<Mandatory? 0/1>|<Extent? 0/1>|<Part of index? 0/1>|<Part of PK? 0/1>|<Initial value>|<Desription>
                        if (splitted.Length != 12 || currentTable == null)
                            return;
                        var flags = splitted[6].Equals("1") ? ParseFlag.Mandatory : 0;
                        if (splitted[7].Equals("1")) flags = flags | ParseFlag.Extent;
                        if (splitted[8].Equals("1")) flags = flags | ParseFlag.Index;
                        if (splitted[9].Equals("1")) flags = flags | ParseFlag.Primary;
                        var curField = new ParsedField(
                            splitted[2],
                            splitted[3],
                            splitted[4],
                            int.Parse(splitted[5]),
                            flags,
                            splitted[10],
                            splitted[11],
                            ParsedAsLike.None);
                        curField.Type = ParserUtils.ConvertStringToParsedPrimitiveType(curField.TempType);
                        currentTable.Fields.Add(curField);
                        break;
                }
            });
        }

        #endregion

        #region get list

        /// <summary>
        /// returns a dictionary containing all the table names of each database, 
        /// each table is present 2 times, as "TABLE" and "DATABASE.TABLE"
        /// </summary>
        /// <returns></returns>
        public Dictionary<string, CompletionType> GetDbDictionary(Dictionary<string, CompletionType> list) {
            _dataBases.ForEach(@base => @base.Tables.ForEach(table => {
                if (!list.ContainsKey(table.Name))
                    list.Add(table.Name, CompletionType.Table);
                if (!list.ContainsKey(string.Join(".", @base.Name, table.Name)))
                    list.Add(string.Join(".", @base.Name, table.Name), CompletionType.Table);
            }));
            return list;
        }

        /// <summary>
        /// Allows to recompute the database list of completion item (when changing case for instance)
        /// </summary>
        public void ResetCompletionItems() {
            _dbItems = GetCompletionItems();
            if (OnDatabaseUpdate != null)
                OnDatabaseUpdate();
        }

        /// <summary>
        /// List of items for the autocompletion
        /// </summary>
        public List<CompletionItem> CompletionItems {
            get { return _dbItems ?? (_dbItems = GetCompletionItems()); }
        }

        private List<CompletionItem> GetCompletionItems() {
            // Sequences
            var output = _sequences.Select(item => new SequenceCompletionItem {
                DisplayText = item.SeqName.ConvertCase(Config.Instance.AutoCompleteDatabaseWordCaseMode),
                SubText = item.DbName
            }).Cast<CompletionItem>().ToList();

            // Databases
            foreach (var db in _dataBases) {
                var curDb = new DatabaseCompletionItem {
                    DisplayText = db.Name.ConvertCase(Config.Instance.AutoCompleteDatabaseWordCaseMode),
                    ParsedBaseItem = db,
                    Ranking = 0,
                    Flags = 0,
                    Children = new List<CompletionItem>(),
                    ChildSeparator = '.'
                };
                output.Add(curDb);

                // Tables
                foreach (var table in db.Tables) {
                    var curTable = new TableCompletionItem {
                        DisplayText = table.Name.ConvertCase(Config.Instance.AutoCompleteDatabaseWordCaseMode),
                        SubText = db.Name,
                        ParsedBaseItem = table,
                        Ranking = 0,
                        Flags = 0,
                        Children = new List<CompletionItem>(),
                        ChildSeparator = '.',
                        ParentItem = curDb
                    };
                    curDb.Children.Add(curTable); // add the table as a child of db
                    output.Add(curTable); // but also as an item

                    // Fields
                    foreach (var field in table.Fields) {
                        CompletionItem curField = CompletionItem.Factory.New(field.Flags.HasFlag(ParseFlag.Primary) ? CompletionType.FieldPk : CompletionType.Field);
                        curField.DisplayText = field.Name.ConvertCase(Config.Instance.AutoCompleteDatabaseWordCaseMode);
                        curField.SubText = field.Type.ToString();
                        curField.ParsedBaseItem = field;
                        curField.Ranking = 0;
                        curField.Flags = field.Flags & ~ParseFlag.Primary;
                        curField.ParentItem = curTable;
                        curTable.Children.Add(curField);
                    }
                }
            }
            return output;
        }

        #endregion

        #region find item

        public ParsedDataBase FindDatabaseByName(string name) {
            return _dataBases.Find(@base => @base.Name.EqualsCi(name));
        }

        public ParsedTable FindTableByName(string name, ParsedDataBase db) {
            return db.Tables.Find(table => table.Name.EqualsCi(name));
        }

        /// <summary>
        /// Find the table referenced among database (can be BASE.TABLE)
        /// </summary>
        public ParsedTable FindTableByName(string name) {
            if (name.CountOccurences(".") > 0) {
                var splitted = name.Split('.');
                // find db then find table
                var foundDb = FindDatabaseByName(splitted[0]);
                return foundDb == null ? null : FindTableByName(splitted[1], foundDb);
            }
            return _dataBases.Select(dataBase => FindTableByName(name, dataBase)).FirstOrDefault(found => found != null);
        }

        public ParsedField FindFieldByName(string name, ParsedTable table) {
            return table.Fields.Find(field => field.Name.EqualsCi(name));
        }

        /// <summary>
        /// Returns the field corresponding to the input TABLE.FIELD or DB.TABLE.FIELD
        /// </summary>
        public ParsedField FindFieldByName(string name) {
            var splitted = name.Split('.');
            if (splitted.Length == 1)
                return null;

            var tableName = splitted[splitted.Length == 3 ? 1 : 0];
            var fieldName = splitted[splitted.Length == 3 ? 2 : 1];

            ParsedTable foundTable;

            if (splitted.Length == 3) {
                // find db
                var foundDb = FindDatabaseByName(splitted[0]);
                if (foundDb == null)
                    return null;

                // find table
                foundTable = FindTableByName(tableName, foundDb);
            } else {
                // find table
                foundTable = FindTableByName(tableName);
            }

            // find field
            return foundTable == null ? null : FindFieldByName(fieldName, foundTable);
        }

        #endregion
    }
}