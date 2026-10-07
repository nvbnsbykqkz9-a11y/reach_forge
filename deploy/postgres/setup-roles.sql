-- ReachForge の PostgreSQL ロール設定（初回のみ、管理者で実行）。
-- ・reachforge_owner：表の所有者。CI/CD のマイグレーション（dotnet ef / migrations bundle）だけが使う
-- ・reachforge_app ：アプリ（Web / Worker）が使う。行レベルセキュリティ（RLS）を回避できない（NOSUPERUSER・NOBYPASSRLS）
-- パスワードは Key Vault などで管理し、このファイルに書かないこと（psql -v で渡す）。
--   psql -v owner_password=... -v app_password=... -d reachforge -f setup-roles.sql

CREATE ROLE reachforge_owner LOGIN PASSWORD :'owner_password' NOSUPERUSER NOCREATEROLE;
CREATE ROLE reachforge_app   LOGIN PASSWORD :'app_password'   NOSUPERUSER NOCREATEROLE NOBYPASSRLS;

GRANT CONNECT ON DATABASE reachforge TO reachforge_owner, reachforge_app;
GRANT USAGE, CREATE ON SCHEMA public TO reachforge_owner;
GRANT USAGE ON SCHEMA public TO reachforge_app;

-- マイグレーションで今後作られる表・シーケンスにも、アプリ用ロールの権限を自動で付ける
ALTER DEFAULT PRIVILEGES FOR ROLE reachforge_owner IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO reachforge_app;
ALTER DEFAULT PRIVILEGES FOR ROLE reachforge_owner IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO reachforge_app;
ALTER DEFAULT PRIVILEGES FOR ROLE reachforge_owner IN SCHEMA public
    GRANT EXECUTE ON FUNCTIONS TO reachforge_app;
