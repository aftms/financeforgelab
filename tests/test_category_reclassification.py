import copy
import importlib.util
import json
import sqlite3
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('reclassify', Path(__file__).resolve().parents[1] / 'tools/Reclassify-Categories.py')
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class CategoryConversionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.path = self.root / 'source.sqlite'
        with sqlite3.connect(self.path) as db:
            db.executescript('''
CREATE TABLE Categories(Id INTEGER PRIMARY KEY,HouseholdId INTEGER,ParentId INTEGER REFERENCES Categories(Id),Name TEXT,Kind INTEGER,Active INTEGER,Version INTEGER);
CREATE TABLE Transactions(Id INTEGER PRIMARY KEY,HouseholdId INTEGER,Date TEXT,Kind INTEGER,Description TEXT,Notes TEXT,PersonId INTEGER,Origin TEXT,Status TEXT,Version INTEGER);
CREATE TABLE Postings(Id INTEGER PRIMARY KEY,TransactionId INTEGER REFERENCES Transactions(Id),CategoryId INTEGER REFERENCES Categories(Id),AccountId INTEGER,AmountUnits INTEGER,Currency TEXT,Date TEXT,ValueDate TEXT);
CREATE TABLE BankImports(Id INTEGER PRIMARY KEY,Status TEXT,ReviewJson TEXT,Version INTEGER);
CREATE TABLE BankImportEntries(Id INTEGER PRIMARY KEY,TransactionId INTEGER REFERENCES Transactions(Id));
CREATE TABLE AuditChanges(Id INTEGER PRIMARY KEY,Entity TEXT,EntityId INTEGER,Action TEXT,Actor TEXT,CreatedUtc TEXT,DetailJson TEXT);
INSERT INTO Categories VALUES(1,1,NULL,'Old',2,1,1),(2,1,1,'Known',2,1,1),(3,1,1,'Unknown',2,1,1);
INSERT INTO Transactions VALUES(1,1,'2019-01-01',2,'expense','keep notes',1,'Legacy','Confirmed',1),(2,1,'2019-01-02',2,'refund',NULL,1,'Legacy','Confirmed',1),(3,1,'2019-01-03',2,'unknown',NULL,1,'Legacy','Confirmed',1);
INSERT INTO Postings VALUES(1,1,2,NULL,-123456,'EUR','2019-01-01',NULL),(2,2,2,NULL,10000,'EUR','2019-01-02',NULL),(3,3,3,NULL,-50000,'EUR','2019-01-03',NULL);
''')
        self.plan = {'approved': True, 'batch_id': 'test-v1', 'baseline': self.state(), 'source_category_ids': [1,2,3], 'categories': [{'key':'new','name':'New','kind':2},{'key':'child','name':'Known','kind':2,'parent_key':'new'}], 'assignments': [{'posting_id':1,'source_category_id':2,'target_key':'child'},{'posting_id':2,'source_category_id':2,'target_key':'child'}], 'pending': [{'posting_id':3,'source_category_id':3,'reason':'Unknown'}]}

    def tearDown(self):
        self.temp.cleanup()

    def state(self):
        with mod.connect(self.path, True) as db:
            return mod.fingerprint(db)

    def apply(self, plan=None, backup='before.sqlite'):
        return mod.apply_plan(self.path, plan or self.plan, self.root / backup)

    def test_preserves_values_refunds_references_and_pending_with_idempotence(self):
        with mod.connect(self.path, True) as db:
            original = mod.ledger_fingerprint(db)
        result = self.apply()
        self.assertEqual(2,result['changed_postings'])
        self.assertEqual(1,result['pending_postings'])
        with mod.connect(self.path, True) as db:
            self.assertEqual(original,mod.ledger_fingerprint(db))
            self.assertEqual(3,db.execute('SELECT CategoryId FROM Postings WHERE Id=3').fetchone()[0])
            self.assertEqual([2,2,1],[r[0] for r in db.execute('SELECT Version FROM Transactions ORDER BY Id')])
            self.assertEqual(0,db.execute('SELECT Active FROM Categories WHERE Id=3').fetchone()[0])
            self.assertEqual(1,db.execute('SELECT count(*) FROM AuditChanges').fetchone()[0])
        state=self.state()
        self.assertTrue(self.apply()['already_applied'])
        self.assertEqual(state,self.state())
        with mod.connect(self.root/'before.sqlite',True) as db:
            self.assertEqual(self.plan['baseline'],mod.fingerprint(db))

    def test_unapproved_or_stale_plan_cannot_write(self):
        before=self.state()
        bad=copy.deepcopy(self.plan);bad['approved']=False
        with self.assertRaisesRegex(ValueError,'ApprovedPlanRequired'):self.apply(bad)
        bad=copy.deepcopy(self.plan);bad['baseline']['Postings']='wrong'
        with self.assertRaisesRegex(ValueError,'BaselineChanged'):self.apply(bad)
        self.assertEqual(before,self.state())
        self.assertFalse((self.root/'before.sqlite').exists())

    def test_failure_after_category_creation_rolls_back_every_change(self):
        before=self.state()
        bad=copy.deepcopy(self.plan)
        for c in bad['categories']:c['kind']=1
        with self.assertRaisesRegex(ValueError,'CategoryKindOrHouseholdConflict'):self.apply(bad)
        self.assertEqual(before,self.state())
        with mod.connect(self.path,True) as db:self.assertEqual(0,db.execute('SELECT count(*) FROM AuditChanges').fetchone()[0])

    def test_missing_or_duplicate_postings_are_rejected(self):
        for name,changes,error in [('missing',self.plan['assignments'][:1],'PostingCoverage'),('duplicate',self.plan['assignments']*2,'DuplicatePosting')]:
            bad=copy.deepcopy(self.plan);bad['assignments']=changes
            with self.assertRaisesRegex(ValueError,error):self.apply(bad,name+'.sqlite')
            self.assertEqual(self.plan['baseline'],self.state())

    def test_saved_bank_draft_blocks_retiring_its_selected_category(self):
        with sqlite3.connect(self.path) as db:db.execute('INSERT INTO BankImports VALUES(1,?,?,1)',('Draft',json.dumps({'Rows':[{'CategoryId':2}]})))
        self.plan['baseline']=self.state()
        with self.assertRaisesRegex(ValueError,'SavedBankReviewRequiresExplicitMapping'):self.apply()
        self.assertEqual(self.plan['baseline'],self.state())

    def test_bank_linked_transaction_requires_explicit_review(self):
        with sqlite3.connect(self.path) as db:db.execute('INSERT INTO BankImportEntries VALUES(1,1)')
        self.plan['baseline']=self.state()
        with self.assertRaisesRegex(ValueError,'BankLinkedTransactionRequiresExplicitReview'):self.apply()
        self.assertEqual(self.plan['baseline'],self.state())

    def test_rollback_restores_categories_without_reusing_stale_versions(self):
        self.apply()
        result=mod.rollback(self.path,'test-v1',self.root/'before-rollback.sqlite')
        self.assertEqual(2,result['restored_postings'])
        with mod.connect(self.path,True) as db:
            self.assertEqual([2,2,3],[r[0] for r in db.execute('SELECT CategoryId FROM Postings ORDER BY Id')])
            self.assertEqual([3,3,1],[r[0] for r in db.execute('SELECT Version FROM Transactions ORDER BY Id')])
            self.assertEqual(3,db.execute('SELECT count(*) FROM Categories WHERE Active=1').fetchone()[0])
            self.assertEqual(2,db.execute('SELECT count(*) FROM AuditChanges').fetchone()[0])
        self.assertTrue(mod.rollback(self.path,'test-v1',self.root/'unused.sqlite')['already_rolled_back'])
        with self.assertRaisesRegex(ValueError,'BatchIdentityConflict'):self.apply(backup='again.sqlite')

    def test_rollback_rejects_later_edit_and_batch_id_rejects_different_plan(self):
        self.apply()
        bad=copy.deepcopy(self.plan);bad['approval']='different'
        with self.assertRaisesRegex(ValueError,'BatchIdentityConflict'):self.apply(bad,'another.sqlite')
        with sqlite3.connect(self.path) as db:db.execute("UPDATE Transactions SET Notes='new edit',Version=Version+1 WHERE Id=1")
        state=self.state()
        with self.assertRaisesRegex(ValueError,'LaterChangesPreventRollback'):mod.rollback(self.path,'test-v1',self.root/'undo.sqlite')
        self.assertEqual(state,self.state())

    def test_backup_destination_cannot_be_overwritten(self):
        (self.root/'before.sqlite').write_text('valuable backup')
        with self.assertRaises(FileExistsError):self.apply()
        self.assertEqual(self.plan['baseline'],self.state())
        self.assertEqual('valuable backup',(self.root/'before.sqlite').read_text())


if __name__=='__main__':unittest.main()