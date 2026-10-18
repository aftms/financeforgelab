"""Audited, offline category reclassification. Plans and reports contain private data."""
import argparse
import hashlib
import json
import sqlite3
from datetime import datetime, timezone
from pathlib import Path

GUARDED = ('Categories', 'Transactions', 'Postings', 'BankImports', 'BankImportEntries')


def canonical(value):
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(',', ':'))


def digest(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def connect(path, readonly=False):
    path = Path(path).resolve()
    if not path.is_file():
        raise ValueError('DatabaseNotFound')
    db = sqlite3.connect(path.as_uri() + ('?mode=ro' if readonly else '?mode=rw'), uri=True)
    db.row_factory = sqlite3.Row
    db.execute('PRAGMA foreign_keys=ON')
    db.execute('PRAGMA busy_timeout=10000')
    return db


def records(db, table):
    if table not in GUARDED:
        raise ValueError('UnknownTable')
    return [dict(r) for r in db.execute(f'SELECT * FROM "{table}" ORDER BY Id')]


def fingerprint(db):
    return {table: digest(records(db, table)) for table in GUARDED}


def ledger_fingerprint(db):
    transactions = [{k: v for k, v in r.items() if k != 'Version'} for r in records(db, 'Transactions')]
    postings = [{k: v for k, v in r.items() if k != 'CategoryId'} for r in records(db, 'Postings')]
    return digest([transactions, postings])


def backup(source, destination):
    destination = Path(destination).resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open('xb'):
        pass
    with connect(source, True) as src, sqlite3.connect(destination) as dst:
        src.backup(dst)
        if dst.execute('PRAGMA integrity_check').fetchone()[0] != 'ok':
            raise ValueError('BackupIntegrityFailed')


def audit(db, entity, entity_id, action, detail):
    db.execute('INSERT INTO AuditChanges(Entity,EntityId,Action,Actor,CreatedUtc,DetailJson) VALUES(?,?,?,?,?,?)',
               (entity, entity_id, action, 'CategoryMigration', datetime.now(timezone.utc).isoformat(), canonical(detail)))


def previous(db, batch_id, action):
    for row in db.execute("SELECT DetailJson FROM AuditChanges WHERE Entity='CategoryRestructure' AND Action=? ORDER BY Id DESC", (action,)):
        detail = json.loads(row[0])
        if detail.get('batch_id') == batch_id:
            return detail
    return None


def integrity(db):
    if db.execute('PRAGMA integrity_check').fetchone()[0] != 'ok' or db.execute('PRAGMA foreign_key_check').fetchone():
        raise ValueError('IntegrityFailed')


def apply_plan(database, plan, backup_path):
    if not plan.get('approved') or not plan.get('batch_id'):
        raise ValueError('ApprovedPlanRequired')
    plan_hash = digest(plan)
    with connect(database, True) as read:
        old = previous(read, plan['batch_id'], 'Applied')
        if old:
            if old['plan_hash'] != plan_hash or previous(read, plan['batch_id'], 'RolledBack'):
                raise ValueError('BatchIdentityConflict')
            return {**old['summary'], 'already_applied': True}
        if fingerprint(read) != plan['baseline']:
            raise ValueError('BaselineChanged')
    backup(database, backup_path)
    db = connect(database)
    try:
        db.execute('BEGIN IMMEDIATE')
        if fingerprint(db) != plan['baseline']:
            raise ValueError('BaselineChanged')
        ledger_before = ledger_fingerprint(db)
        cats_before = records(db, 'Categories')
        old_ids = {c['Id'] for c in cats_before}
        if set(plan['source_category_ids']) != old_ids or len(plan['source_category_ids']) != len(old_ids):
            raise ValueError('SourceCategoryCoverage')
        cats = {c['Id']: c for c in cats_before}
        posts = {r['Id']: r for r in records(db, 'Postings')}
        transactions = {r['Id']: r for r in records(db, 'Transactions')}
        specifications = {c['key']: c for c in plan['categories']}
        if len(specifications) != len(plan['categories']):
            raise ValueError('DuplicateTargetKey')
        if any(not c['name'].strip() or len(c['name']) > 100 or c['kind'] not in (1, 2) for c in specifications.values()):
            raise ValueError('InvalidCategory')
        households = {c['HouseholdId'] for c in cats_before}
        if len(households) != 1:
            raise ValueError('SingleHouseholdRequired')
        household = next(iter(households))
        assignments = plan['assignments']
        if len({a['posting_id'] for a in assignments}) != len(assignments):
            raise ValueError('DuplicatePosting')
        pending = plan['pending']
        if len({a['posting_id'] for a in pending}) != len(pending):
            raise ValueError('DuplicatePendingPosting')
        if {a['posting_id'] for a in assignments} & {a['posting_id'] for a in pending}:
            raise ValueError('AssignedAndPending')
        eligible = {p['Id'] for p in posts.values() if p['CategoryId'] in old_ids}
        if {a['posting_id'] for a in assignments + pending} != eligible:
            raise ValueError('PostingCoverage')
        targets = {}; created_ids = []
        for category in sorted(specifications.values(), key=lambda c: bool(c.get('parent_key'))):
            parent = category.get('parent_key')
            if parent and (parent not in targets or specifications[parent].get('parent_key') or specifications[parent]['kind'] != category['kind']):
                raise ValueError('InvalidHierarchy')
            parent_id = targets[parent] if parent else None
            same = [c for c in cats.values() if c['Kind'] == category['kind'] and c['ParentId'] == parent_id and c['Name'].casefold() == category['name'].casefold()]
            if len(same) > 1:
                raise ValueError('AmbiguousExistingCategory')
            if same:
                target_id = same[0]['Id']
            else:
                target_id = db.execute('INSERT INTO Categories(HouseholdId,ParentId,Name,Kind,Active,Version) VALUES(?,?,?,?,1,1)',
                                       (household, parent_id, category['name'], category['kind'])).lastrowid
                cats[target_id] = dict(db.execute('SELECT * FROM Categories WHERE Id=?', (target_id,)).fetchone())
                created_ids.append(target_id)
            if target_id in targets.values():
                raise ValueError('DuplicateTargetCategory')
            targets[category['key']] = target_id
        changes = []
        for assignment in assignments:
            post = posts.get(assignment['posting_id'])
            category = specifications.get(assignment['target_key'])
            if not post or not category or not category.get('parent_key') or post['CategoryId'] != assignment['source_category_id']:
                raise ValueError('PostingMappingConflict')
            transaction = transactions[post['TransactionId']]
            if transaction['Kind'] != category['kind'] or transaction['HouseholdId'] != household or cats[post['CategoryId']]['Kind'] != category['kind']:
                raise ValueError('CategoryKindOrHouseholdConflict')
            target_id = targets[assignment['target_key']]
            if target_id != post['CategoryId']:
                changes.append({'posting_id': post['Id'], 'transaction_id': post['TransactionId'], 'before_category_id': post['CategoryId'], 'after_category_id': target_id})
        for item in pending:
            if posts[item['posting_id']]['CategoryId'] != item['source_category_id']:
                raise ValueError('PendingMappingConflict')
            if item['source_category_id'] in targets.values():
                raise ValueError('PendingCategoryMustRemainArchived')
        retiring = old_ids - set(targets.values())
        for row in records(db, 'BankImports'):
            if row['Status'] == 'Draft' and row['ReviewJson']:
                review = json.loads(row['ReviewJson'])
                if any(r.get('CategoryId') in retiring for r in review.get('Rows', [])):
                    raise ValueError('SavedBankReviewRequiresExplicitMapping')
        changed_transactions = {x['transaction_id'] for x in changes}
        if any(row['TransactionId'] in changed_transactions for row in records(db, 'BankImportEntries')):
            raise ValueError('BankLinkedTransactionRequiresExplicitReview')
        for change in changes:
            db.execute('UPDATE Postings SET CategoryId=? WHERE Id=?', (change['after_category_id'], change['posting_id']))
        transaction_versions = []
        for transaction_id in sorted(changed_transactions):
            old_version = transactions[transaction_id]['Version']
            db.execute('UPDATE Transactions SET Version=Version+1 WHERE Id=?', (transaction_id,))
            transaction_versions.append({'id': transaction_id, 'before': old_version, 'after': old_version + 1})
        changed_category_ids = []
        for category in cats_before:
            active = int(category['Id'] in targets.values())
            if category['Active'] != active:
                db.execute('UPDATE Categories SET Active=?,Version=Version+1 WHERE Id=?', (active, category['Id']))
                changed_category_ids.append(category['Id'])
        if ledger_before != ledger_fingerprint(db):
            raise ValueError('LedgerChanged')
        integrity(db)
        summary = {'batch_id': plan['batch_id'], 'resolved_postings': len(assignments), 'changed_postings': len(changes), 'changed_transactions': len(changed_transactions), 'pending_postings': len(pending), 'active_parents': sum(not c.get('parent_key') for c in specifications.values()), 'active_subcategories': sum(bool(c.get('parent_key')) for c in specifications.values()), 'archived_categories': len(retiring), 'financial_data_unchanged': True}
        detail = {'batch_id': plan['batch_id'], 'plan_hash': plan_hash, 'summary': summary, 'categories_before': cats_before, 'created_category_ids': created_ids, 'category_status_changed_ids': changed_category_ids, 'posting_changes': changes, 'transaction_versions': transaction_versions, 'pending': pending, 'before': plan['baseline'], 'after': fingerprint(db), 'ledger_sha256': ledger_before}
        audit(db, 'CategoryRestructure', 0, 'Applied', detail)
        db.commit()
        return summary
    except BaseException:
        db.rollback()
        raise
    finally:
        db.close()


def rollback(database, batch_id, backup_path):
    with connect(database, True) as read:
        old = previous(read, batch_id, 'Applied')
        if not old:
            raise ValueError('BatchNotFound')
        if previous(read, batch_id, 'RolledBack'):
            return {'batch_id': batch_id, 'already_rolled_back': True}
        if fingerprint(read) != old['after']:
            raise ValueError('LaterChangesPreventRollback')
    backup(database, backup_path)
    db = connect(database)
    try:
        db.execute('BEGIN IMMEDIATE')
        if fingerprint(db) != old['after']:
            raise ValueError('LaterChangesPreventRollback')
        for change in old['posting_changes']:
            db.execute('UPDATE Postings SET CategoryId=? WHERE Id=?', (change['before_category_id'], change['posting_id']))
        for version in old['transaction_versions']:
            db.execute('UPDATE Transactions SET Version=Version+1 WHERE Id=?', (version['id'],))
        for category in old['categories_before']:
            if category['Id'] in old['category_status_changed_ids']:
                db.execute('UPDATE Categories SET Active=?,Version=Version+1 WHERE Id=?', (category['Active'], category['Id']))
        for category_id in old['created_category_ids']:
            db.execute('UPDATE Categories SET Active=0,Version=Version+1 WHERE Id=?', (category_id,))
        if ledger_fingerprint(db) != old['ledger_sha256']:
            raise ValueError('LedgerChanged')
        integrity(db)
        audit(db, 'CategoryRestructure', 0, 'RolledBack', {'batch_id': batch_id, 'plan_hash': old['plan_hash'], 'restored_postings': len(old['posting_changes'])})
        db.commit()
        return {'batch_id': batch_id, 'restored_postings': len(old['posting_changes']), 'financial_data_unchanged': True}
    except BaseException:
        db.rollback()
        raise
    finally:
        db.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['apply', 'rollback'])
    parser.add_argument('--database', required=True)
    parser.add_argument('--backup', required=True, help='New backup file, never overwritten')
    parser.add_argument('--plan')
    parser.add_argument('--batch-id')
    parser.add_argument('--report', required=True, help='New private report, never overwritten')
    args = parser.parse_args()
    report = Path(args.report)
    if report.exists():
        parser.error('ReportAlreadyExists')
    try:
        if args.operation == 'apply':
            if not args.plan:
                parser.error('--plan required')
            result = apply_plan(args.database, json.loads(Path(args.plan).read_text(encoding='utf-8')), args.backup)
        else:
            if not args.batch_id:
                parser.error('--batch-id required')
            result = rollback(args.database, args.batch_id, args.backup)
        report.parent.mkdir(parents=True, exist_ok=True)
        with report.open('x', encoding='utf-8') as stream:
            json.dump(result, stream, ensure_ascii=False, indent=2)
        print(canonical(result))
        return 0
    except Exception as error:
        print('Category conversion stopped: ' + type(error).__name__ + ': ' + str(error))
        return 1


if __name__ == '__main__':
    raise SystemExit(main())