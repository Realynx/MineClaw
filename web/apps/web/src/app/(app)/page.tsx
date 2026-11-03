import { redirect } from 'next/navigation';

/** Live is the default landing page. */
export default function Home() {
  redirect('/live');
}
