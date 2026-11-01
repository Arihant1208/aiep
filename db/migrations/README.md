# Database migrations

Versioned, forward-only SQL migrations applied by the `aiep_migrator` role.
The tool and conventions are pending [ADR-0013](../../docs/adr/0013-data-access-and-migrations.md).

Planned conventions:

- File name: `V0001__short_description.sql`, applied in order, never edited after merge.
- Every tenant-owned table has `organization_id`, RLS enabled **and** forced, and composite foreign keys
  that keep children in the parent's tenant (see [data model](../../docs/data/data-model.md)).
- Expand/contract for breaking changes: add new columns or tables first, migrate data, remove the old ones in a later release.
- Running services never connect as the schema owner.
