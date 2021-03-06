﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using JetBrains.Annotations;

namespace Mute.Services
{
    public class IouDatabaseService
    {
        #region SQL debts
        private const string InsertDebtSql = "INSERT INTO `IOU_Debts` (`LenderId`, `BorrowerId`, `Amount`, `Unit`, `Note`) VALUES (@LenderId, @BorrowerId, @Amount, @Unit, @Note)";

        private const string FindOwedByPerson = @"SELECT *
            FROM (
                SELECT `lent`.`Unit`, `lent`.`Amount` - ifnull(`borrowed`.`Amount`, 0) AS 'Amount', `LenderId`, @PersonId as `BorrowerId`
            FROM (
                SELECT `Unit`, Sum(`Amount`) as 'Amount', `LenderId`
            FROM IOU_Debts
            WHERE `BorrowerId` = @PersonId
            GROUP BY `Unit`, `LenderId`
            ) lent
                LEFT OUTER JOIN (
                SELECT `Unit`, Sum(`Amount`) as 'Amount', `BorrowerId`
            FROM IOU_Debts
            WHERE `LenderId` = @PersonId
            GROUP BY `Unit`, `BorrowerId`
            ) borrowed
                ON `lent`.`LenderId` = `borrowed`.`BorrowerId` AND `lent`.`Unit` = `borrowed`.`Unit`
                WHERE (`lent`.`Unit` = @Unit or @Unit is NULL)
            )
            WHERE `Amount` > 0";

        private const string FindLentByPerson = @"SELECT *
            FROM (
                SELECT `lent`.`Unit`, `lent`.`Amount` - ifnull(`borrowed`.`Amount`, 0) AS 'Amount', `BorrowerId`, @PersonId as `LenderId`
            FROM (
                SELECT `Unit`, Sum(`Amount`) as 'Amount', `BorrowerId`
            FROM IOU_Debts
            WHERE `LenderId` = @PersonId
            GROUP BY `Unit`, `BorrowerId`
            ) lent
                LEFT OUTER JOIN (
                SELECT `Unit`, Sum(`Amount`) as 'Amount', `LenderId`
            FROM IOU_Debts
            WHERE `BorrowerId` = @PersonId
            GROUP BY `Unit`, `LenderId`
            ) borrowed
                ON `lent`.`BorrowerId` = `borrowed`.`LenderId` AND `lent`.`Unit` = `borrowed`.`Unit`
                WHERE (`lent`.`Unit` = @Unit or @Unit is NULL)
            )
            WHERE `Amount` > 0";
        #endregion

        #region sql payments
        private const string InsertPaymentSql = "INSERT INTO `IOU_PendingPayments` (`ID`, `PayerId`, `ReceiverId`, `Amount`, `Unit`, `Note`) VALUES (@Id, @PayerId, @ReceiverId, @Amount, @Unit, @Note)";

        private const string FindPaymentsByReceiver = "SELECT * FROM IOU_PendingPayments WHERE ReceiverId = @ReceiverId AND Confirmed = 'false'";

        private const string ConfirmPayment = @"BEGIN TRANSACTION;
	        INSERT INTO IOU_Debts (LenderId, BorrowerId, Amount, Unit, Note)
	        SELECT PayerId, ReceiverId, Amount, Unit, '(payment:'||ID||') '||Note
            FROM IOU_PendingPayments
            WHERE ID = @PaymentId
            AND Confirmed = 'false';

            UPDATE IOU_PendingPayments
            SET Confirmed = 'true'
            WHERE ID = @PaymentId;
	    
            SELECT * FROM IOU_PendingPayments
                WHERE ID = @PaymentId
            AND Confirmed = 'true';
	    
        COMMIT;";
        #endregion

        private readonly DatabaseService _database;

        public IouDatabaseService(DatabaseService database)
        {
            _database = database;

            _database.Exec("CREATE TABLE IF NOT EXISTS `IOU_Debts` (`ID` INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT UNIQUE, `LenderId` TEXT NOT NULL, `BorrowerId` TEXT NOT NULL, `Amount` TEXT NOT NULL, `Unit` TEXT NOT NULL, `Note` TEXT);");
            _database.Exec("CREATE INDEX IF NOT EXISTS `DebtsIndexBorrowerLeading` ON `IOU_Debts` ( `BorrowerId` ASC, `Unit` ASC, `LenderId` ASC, `Amount` ASC )");
            _database.Exec("CREATE INDEX IF NOT EXISTS `DebtsIndexLenderLeading` ON `IOU_Debts` ( `LenderId` ASC, `Unit` ASC, `BorrowerId` ASC, `Amount` ASC )");

            _database.Exec("CREATE TABLE IF NOT EXISTS `IOU_PendingPayments` (`ID` TEXT NOT NULL UNIQUE, `PayerId` TEXT NOT NULL, `ReceiverId` TEXT NOT NULL, `Amount` TEXT NOT NULL, `Unit` TEXT NOT NULL, `Note` TEXT, `Confirmed` BOOL NOT NULL DEFAULT 'false', PRIMARY KEY(`ID`) )");
            _database.Exec("CREATE INDEX IF NOT EXISTS `PendingPaymentsIndexByReceiver` ON `IOU_PendingPayments` (`ReceiverId` ASC)");
        }

        #region debts
        public async Task InsertDebt([NotNull] IUser lender, [NotNull] IUser borrower, decimal amount, [NotNull] string unit, [CanBeNull] string note)
        {
            using (var cmd = _database.CreateCommand())
            {
                cmd.CommandText = InsertDebtSql;
                cmd.Parameters.Add(new SQLiteParameter("@LenderId", System.Data.DbType.String) { Value = lender.Id.ToString() });
                cmd.Parameters.Add(new SQLiteParameter("@BorrowerId", System.Data.DbType.String) { Value = borrower.Id.ToString() });
                cmd.Parameters.Add(new SQLiteParameter("@Amount", System.Data.DbType.String) { Value = amount.ToString(CultureInfo.InvariantCulture) });
                cmd.Parameters.Add(new SQLiteParameter("@Unit", System.Data.DbType.String) { Value = unit.ToLowerInvariant() });
                cmd.Parameters.Add(new SQLiteParameter("@Note", System.Data.DbType.String) { Value = note ?? "" });

                await cmd.ExecuteNonQueryAsync();
            }
        }

        [ItemNotNull]
        private static async Task<IReadOnlyList<Owed>> ParseOwed([NotNull] DbDataReader reader)
        {
            var debts = new List<Owed>();

            while (await reader.ReadAsync())
            {
                debts.Add(new Owed(
                    ulong.Parse((string)reader["LenderId"]),
                    ulong.Parse((string)reader["BorrowerId"]),
                    decimal.Parse(reader["Amount"].ToString()),
                    (string)reader["Unit"])
                );
            }

            return debts;
        }

        [ItemNotNull] public async Task<IReadOnlyList<Owed>> GetOwed([NotNull] IUser borrower)
        {
            return await GetOwed(borrower.Id, null);
        }

        [ItemNotNull] private async Task<IReadOnlyList<Owed>> GetOwed(ulong borrowerId, [CanBeNull] string unit = null)
        {
            try
            {
                using (var cmd = _database.CreateCommand())
                {
                    cmd.CommandText = FindOwedByPerson;
                    cmd.Parameters.Add(new SQLiteParameter("@PersonId", System.Data.DbType.String) { Value = borrowerId.ToString() });

                    if (unit != null)
                        cmd.Parameters.Add(new SQLiteParameter("@Unit", System.Data.DbType.String) { Value = unit });
                    else
                        cmd.Parameters.Add(new SQLiteParameter("@Unit", System.Data.DbType.String) { Value = DBNull.Value });

                    using (var results = await cmd.ExecuteReaderAsync())
                        return await ParseOwed(results);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        [ItemNotNull]
        public async Task<IReadOnlyList<Owed>> GetLent([NotNull] IUser lender)
        {
            try
            {
                using (var cmd = _database.CreateCommand())
                {
                    cmd.CommandText = FindLentByPerson;
                    cmd.Parameters.Add(new SQLiteParameter("@PersonId", System.Data.DbType.String) { Value = lender.Id.ToString() });
                    cmd.Parameters.Add(new SQLiteParameter("@Unit", System.Data.DbType.String) { Value = DBNull.Value });

                    using (var results = await cmd.ExecuteReaderAsync())
                        return await ParseOwed(results);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }
        #endregion

        #region payments
        public async Task InsertUnconfirmedPayment([NotNull] IUser payer, [NotNull] IUser receiver, decimal amount, [NotNull] string unit, [CanBeNull] string note, [NotNull] string id)
        {
            if (id == null)
                throw new ArgumentNullException(nameof(id));
            try
            {
                using (var cmd = _database.CreateCommand())
                {
                    cmd.CommandText = InsertPaymentSql;
                    cmd.Parameters.Add(new SQLiteParameter("@Id", System.Data.DbType.String) { Value = id });
                    cmd.Parameters.Add(new SQLiteParameter("@PayerId", System.Data.DbType.String) { Value = payer.Id.ToString() });
                    cmd.Parameters.Add(new SQLiteParameter("@ReceiverId", System.Data.DbType.String) { Value = receiver.Id.ToString() });
                    cmd.Parameters.Add(new SQLiteParameter("@Amount", System.Data.DbType.String) { Value = amount.ToString(CultureInfo.InvariantCulture) });
                    cmd.Parameters.Add(new SQLiteParameter("@Unit", System.Data.DbType.String) { Value = unit.ToLowerInvariant() });
                    cmd.Parameters.Add(new SQLiteParameter("@Note", System.Data.DbType.String) { Value = note ?? "" });

                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        [NotNull, ItemNotNull]
        private static async Task<IReadOnlyList<Pending>> ParsePayments([NotNull] DbDataReader reader)
        {
            var debts = new List<Pending>();

            while (await reader.ReadAsync())
            {
                debts.Add(new Pending(
                    ulong.Parse((string)reader["PayerId"]),
                    ulong.Parse((string)reader["ReceiverId"]),
                    decimal.Parse(reader["Amount"].ToString()),
                    (string)reader["Unit"],
                    (string)reader["Note"],
                    (string)reader["ID"]
                ));
            }

            return debts;
        }

        [NotNull, ItemNotNull]
        public async Task<IReadOnlyList<Pending>> GetPending([NotNull] IUser receiver)
        {
            try
            {
                using (var cmd = _database.CreateCommand())
                {
                    cmd.CommandText = FindPaymentsByReceiver;
                    cmd.Parameters.Add(new SQLiteParameter("@ReceiverId", System.Data.DbType.String) { Value = receiver.Id.ToString() });

                    using (var results = await cmd.ExecuteReaderAsync())
                        return await ParsePayments(results);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        public async Task<Pending?> ConfirmPending(string id)
        {
            using (var cmd = _database.CreateCommand())
            {
                cmd.CommandText = ConfirmPayment;
                cmd.Parameters.Add(new SQLiteParameter("@PaymentId", System.Data.DbType.String) { Value = id });
                using (var results = await cmd.ExecuteReaderAsync())
                {
                    if (!results.HasRows)
                        return null;

                    var parsed = await ParsePayments(results);
                    return parsed.Single();
                }
            }
        }
        #endregion

        #region settlement
        public void TrySettle(IUser root, string currency)
        {
            //Find everything this user owes (in this currency)
            var owed = GetOwed(root);


        }
        #endregion
    }

    public struct Owed
    {
        public readonly ulong LenderId;
        public readonly ulong BorrowerId;
        public readonly decimal Amount;
        public readonly string Unit;

        public Owed(ulong lenderId, ulong borrowerId, decimal amount, string unit)
        {
            LenderId = lenderId;
            BorrowerId = borrowerId;
            Amount = amount;
            Unit = unit;
        }
    }

    public struct Pending
    {
        public readonly ulong PayerId;
        public readonly ulong ReceiverId;
        public readonly decimal Amount;
        public readonly string Unit;
        public readonly string Note;
        public readonly string Id;

        public Pending(ulong payerId, ulong receiverId, decimal amount, string unit, string note, string id)
        {
            PayerId = payerId;
            ReceiverId = receiverId;
            Amount = amount;
            Unit = unit;
            Note = note;
            Id = id;
        }
    }
}
