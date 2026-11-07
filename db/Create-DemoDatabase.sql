-- Create-DemoDatabase.sql
--
-- Demo database of SharpPyxis.SqlServer.Facets, and the one its tests run against.
--
-- Run it in a database where Create-FacetsDictionary.sql and Create-FacetsDictionaryProcedures.sql
-- have run, as an administrator. It creates a small shop in the schema shop, with rows: a few
-- customers, products, orders and order lines, all invented. Then three views in the schema facets,
-- which together cover the nine natures, and it annotates them the way an agent would: through the
-- procedures of facets_dict, never by writing its tables.
--
-- Several things are set up on purpose, for the tests and for anyone trying the server. Each one says
-- so where it is.

set nocount on;
go

if schema_id(N'facets_dict') is null or object_id(N'facets_dict.refresh_catalog') is null
begin
    raiserror(N'Run Create-FacetsDictionary.sql and Create-FacetsDictionaryProcedures.sql first.', 16, 1);
    set noexec on;
end;
go

-- ── The shop ─────────────────────────────────────────────────────────────────────────────────────────
-- Amounts are stored in cents, as integers, which is what the divisor of the dictionary exists for.

create schema shop authorization dbo;
go

create table shop.customers
(
    customer_id int           not null,
    name        nvarchar(200) not null,
    email       nvarchar(200) not null,
    city        nvarchar(100) not null,
    is_active   bit           not null,
    created_on  date          not null,
    constraint pk_customers primary key (customer_id)
);

create table shop.products
(
    product_id       int           not null,
    sku              varchar(20)   not null,
    name             nvarchar(200) not null,
    category         varchar(20)   not null,
    unit_price_cents int           not null,
    cost_cents       int           not null,
    constraint pk_products primary key (product_id)
);

create table shop.orders
(
    order_id      int            not null,
    customer_id   int            not null,
    status        char(1)        not null,
    ordered_on    date           not null,
    discount_rate decimal(5, 4)  not null,
    note          nvarchar(max)  null,
    constraint pk_orders primary key (order_id),
    constraint fk_orders_customers foreign key (customer_id) references shop.customers (customer_id),
    constraint ck_orders_status check (status in ('O', 'P', 'C'))
);

create table shop.order_lines
(
    order_id         int            not null,
    line_no          int            not null,
    product_id       int            not null,
    quantity         decimal(18, 3) not null,
    unit_price_cents int            not null,
    constraint pk_order_lines primary key (order_id, line_no),
    constraint fk_order_lines_orders foreign key (order_id) references shop.orders (order_id),
    constraint fk_order_lines_products foreign key (product_id) references shop.products (product_id)
);
go

insert into shop.customers (customer_id, name, email, city, is_active, created_on) values
    (1, N'Atelier Verdon',    N'contact@verdon.example',   N'Nantes',    1, '2024-03-12'),
    (2, N'Brasserie Loiret',  N'achats@loiret.example',    N'Orléans',   1, '2024-07-01'),
    (3, N'Cabinet Marsan',    N'office@marsan.example',    N'Bordeaux',  1, '2025-01-20'),
    (4, N'Dumont & Fille',    N'hello@dumont.example',     N'Nantes',    0, '2023-11-05'),
    (5, N'Épicerie du Port',  N'port@epicerie.example',    N'La Rochelle', 1, '2025-09-15');

insert into shop.products (product_id, sku, name, category, unit_price_cents, cost_cents) values
    (1, 'CHAIR-OAK',  N'Oak chair',          'furniture', 14900, 8200),
    (2, 'TABLE-OAK',  N'Oak table',          'furniture', 64900, 39000),
    (3, 'LAMP-BRASS', N'Brass lamp',         'lighting',  8900,  4100),
    (4, 'LAMP-FLOOR', N'Floor lamp',         'lighting',  19900, 9800),
    (5, 'SHELF-PINE', N'Pine shelf, 120 cm', 'furniture', 7900,  3600);

insert into shop.orders (order_id, customer_id, status, ordered_on, discount_rate, note) values
    (1001, 1, 'P', '2026-01-08', 0.0000, null),
    (1002, 2, 'P', '2026-01-15', 0.0500, N'Delivered to the back entrance.'),
    (1003, 3, 'P', '2026-02-02', 0.0000, null),
    (1004, 1, 'C', '2026-02-10', 0.0000, N'Cancelled by the customer: wrong colour.'),
    (1005, 5, 'P', '2026-02-27', 0.1000, N'Opening order, 10 % off.'),
    (1006, 2, 'P', '2026-03-05', 0.0500, null),
    (1007, 4, 'P', '2026-03-19', 0.0000, N'Last order before the account was closed.'),
    (1008, 3, 'O', '2026-04-07', 0.0000, null),
    (1009, 1, 'O', '2026-04-22', 0.0250, null),
    (1010, 5, 'O', '2026-05-03', 0.0000, N'Asked for an invoice in two parts.');

insert into shop.order_lines (order_id, line_no, product_id, quantity, unit_price_cents) values
    (1001, 1, 1, 4.000, 14900), (1001, 2, 2, 1.000, 64900),
    (1002, 1, 3, 6.000, 8900),  (1002, 2, 5, 3.000, 7900),
    (1003, 1, 4, 2.000, 19900),
    (1004, 1, 1, 2.000, 14900),
    (1005, 1, 5, 10.000, 7900), (1005, 2, 3, 4.000, 8900), (1005, 3, 1, 6.000, 14900),
    (1006, 1, 3, 12.000, 8900),
    (1007, 1, 2, 1.000, 64900), (1007, 2, 1, 6.000, 14900),
    (1008, 1, 4, 1.000, 19900), (1008, 2, 5, 2.000, 7900),
    (1009, 1, 2, 2.000, 64900),
    (1010, 1, 5, 5.000, 7900),  (1010, 2, 3, 2.000, 8900);
go

-- ── The views ────────────────────────────────────────────────────────────────────────────────────────
-- What the agent queries. They belong to dbo, as the tables do: the login reads them through ownership
-- chaining, with no right on shop.

-- One row per order line: the view that carries every nature.
create view facets.v_sales
as
select  l.order_id * 100 + l.line_no                            as sale_id,
        o.order_id,
        c.name                                                  as customer_name,
        c.city                                                  as customer_city,
        p.sku                                                   as product_sku,
        p.name                                                  as product_name,
        p.category,
        o.status,
        o.ordered_on,
        l.quantity,
        -- A unit price is a rate, given in euros with its precision; the line amount is money, in cents.
        cast(l.unit_price_cents / 100.0 as decimal(18, 6))      as unit_price,
        cast(l.quantity * l.unit_price_cents as int)            as line_amount_cents,
        o.discount_rate,
        cast(iif(o.status = 'P', 1, 0) as bit)                  as is_paid,
        o.note
from    shop.order_lines as l
join    shop.orders as o on o.order_id = l.order_id
join    shop.customers as c on c.customer_id = o.customer_id
join    shop.products as p on p.product_id = l.product_id;
go

-- One row per customer, with two measures computed from the orders.
create view facets.v_customers
as
select  c.customer_id,
        c.name,
        c.email,
        c.city,
        c.is_active,
        c.created_on,
        (select count(*) from shop.orders as o where o.customer_id = c.customer_id)     as order_count,
        (select isnull(sum(cast(l.quantity * l.unit_price_cents as int)), 0)
         from   shop.orders as o
         join   shop.order_lines as l on l.order_id = o.order_id
         where  o.customer_id = c.customer_id and o.status = 'P')                       as paid_cents
from    shop.customers as c;
go

-- A view hidden from the model: in the schema, in the dictionary, and not exposed. The agent must not
-- see it, list it, or query it.
create view facets.v_margins
as
select  p.product_id, p.sku, p.unit_price_cents - p.cost_cents as margin_cents
from    shop.products as p;
go

-- ── The dictionary, through its procedures ───────────────────────────────────────────────────────────
declare @by nvarchar(128) = N'Create-DemoDatabase.sql';

exec facets_dict.refresh_catalog @author = @by;

exec facets_dict.write_domain_note @author = @by,
    @note = N'A small furniture and lighting shop. Amounts are in euros, stored in cents. An order is open (O), paid (P) or cancelled (C); only paid orders count as sales. The discount rate applies to the whole order.';

-- v_sales: every nature, with the unit and the divisor on the amounts.
exec facets_dict.annotate_view N'v_sales', @by, @label = N'Sales, one row per order line',
    @answers = N'What was sold, to whom, when, for how much; sales by product, category, city, month or status.';
exec facets_dict.annotate_column N'v_sales', N'sale_id',           @by, @nature_code = N'identifier', @label = N'Sale';
exec facets_dict.annotate_column N'v_sales', N'order_id',          @by, @nature_code = N'identifier', @label = N'Order';
exec facets_dict.annotate_column N'v_sales', N'customer_name',     @by, @nature_code = N'label',      @label = N'Customer';
exec facets_dict.annotate_column N'v_sales', N'customer_city',     @by, @nature_code = N'label',      @label = N'City';
exec facets_dict.annotate_column N'v_sales', N'product_sku',       @by, @nature_code = N'code',       @label = N'Product code';
exec facets_dict.annotate_column N'v_sales', N'product_name',      @by, @nature_code = N'label',      @label = N'Product';
exec facets_dict.annotate_column N'v_sales', N'category',          @by, @nature_code = N'code',       @label = N'Category';
exec facets_dict.annotate_column N'v_sales', N'status',            @by, @nature_code = N'code',       @label = N'Order status';
exec facets_dict.annotate_column N'v_sales', N'ordered_on',        @by, @nature_code = N'date',       @label = N'Order date';
exec facets_dict.annotate_column N'v_sales', N'quantity',          @by, @nature_code = N'quantity',   @label = N'Quantity', @unit = N'pcs';
exec facets_dict.annotate_column N'v_sales', N'unit_price',        @by, @nature_code = N'rate',       @label = N'Unit price', @unit = N'EUR',
    @description = N'Price of one piece: a rate, kept to its precision.';
exec facets_dict.annotate_column N'v_sales', N'line_amount_cents', @by, @nature_code = N'amount',     @label = N'Line amount', @unit = N'EUR', @divisor = 100,
    @description = N'Quantity times unit price, before the discount of the order.';
exec facets_dict.annotate_column N'v_sales', N'discount_rate',     @by, @nature_code = N'rate',       @label = N'Discount rate', @unit = N'%',
    @description = N'Stored as a fraction: 0.05 is 5 %. The same for every line of an order.';
exec facets_dict.annotate_column N'v_sales', N'is_paid',           @by, @nature_code = N'boolean',    @label = N'Paid';
exec facets_dict.annotate_column N'v_sales', N'note',              @by, @nature_code = N'text',       @label = N'Order note';

exec facets_dict.annotate_value N'v_sales', N'status', N'O', @by, @label = N'Open',      @description = N'Placed, not paid yet.';
exec facets_dict.annotate_value N'v_sales', N'status', N'P', @by, @label = N'Paid',      @description = N'Paid in full: a sale.';
exec facets_dict.annotate_value N'v_sales', N'status', N'C', @by, @label = N'Cancelled', @description = N'Cancelled before payment: not a sale.';
exec facets_dict.annotate_value N'v_sales', N'category', N'furniture', @by, @label = N'Furniture';
exec facets_dict.annotate_value N'v_sales', N'category', N'lighting',  @by, @label = N'Lighting';

-- v_customers: a sensitive column, and a column left without a nature.
exec facets_dict.annotate_view N'v_customers', @by, @label = N'Customers, one row each',
    @answers = N'Who the customers are, where, since when, how much each has bought.';
exec facets_dict.annotate_column N'v_customers', N'customer_id', @by, @nature_code = N'identifier', @label = N'Customer';
exec facets_dict.annotate_column N'v_customers', N'name',        @by, @nature_code = N'label',      @label = N'Name';
-- The e-mail may go to an exported file, never to the model: sensitive, and therefore not exposed.
exec facets_dict.annotate_column N'v_customers', N'email',       @by, @nature_code = N'label',      @label = N'E-mail', @is_sensitive = 1, @is_exposed_to_model = 0;
exec facets_dict.annotate_column N'v_customers', N'city',        @by, @nature_code = N'label',      @label = N'City';
exec facets_dict.annotate_column N'v_customers', N'is_active',   @by, @nature_code = N'boolean',    @label = N'Active';
-- created_on is left without a nature, on purpose: the server refuses a column it cannot read.
exec facets_dict.annotate_column N'v_customers', N'order_count', @by, @nature_code = N'quantity',   @label = N'Orders';
exec facets_dict.annotate_column N'v_customers', N'paid_cents',  @by, @nature_code = N'amount',     @label = N'Paid total', @unit = N'EUR', @divisor = 100;

-- v_margins: hidden whole.
exec facets_dict.annotate_view N'v_margins', @by, @label = N'Margins per product', @is_exposed_to_model = 0;

-- A question an agent asked, and its answer.
declare @question table (question_id bigint);
insert into @question exec facets_dict.ask_question @author = N'demo agent',
    @question = N'Does line_amount_cents include the discount of the order?', @view_name = N'v_sales', @column_name = N'line_amount_cents';
declare @question_id bigint = (select question_id from @question);
exec facets_dict.answer_question @question_id, N'No: it is quantity times unit price. Apply discount_rate to get the net amount.', N'demo owner';
go

print N'Demo database ready: schema shop with rows, 3 views in facets, dictionary annotated. Next: Create-FacetsLogin.sql.';
go

set noexec off;
go
