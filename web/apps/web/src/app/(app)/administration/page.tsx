'use client';

import { PageBody } from '@/components/shell/Shell';
import { Card, EmptyState } from '@/components/ui/primitives';
import { AdministrationView } from '@/features/administration/AdministrationView';
import { useAuth } from '@/lib/auth';

export default function AdministrationPage() {
  const { isAdmin } = useAuth();
  if (!isAdmin) {
    return (
      <PageBody>
        <Card>
          <EmptyState title="Administration needs the admin role" />
        </Card>
      </PageBody>
    );
  }
  return (
    <PageBody wide>
      <AdministrationView />
    </PageBody>
  );
}
