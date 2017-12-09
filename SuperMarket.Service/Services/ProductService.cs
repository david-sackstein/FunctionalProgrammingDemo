using System;
using System.Linq;

using FunctionalExtensions;
using SuperMarket.Entities;

namespace SuperMarket.Service
{
    public class ProductService
    {
        private readonly IProductRepository _repository;
        private readonly ISupplierService _supplier;

        public ProductService(IProductRepository repository, ISupplierService supplier)
        {
            _repository = repository;
            _supplier = supplier;
        }

        public HttpResponse CreateProduct(ProductDefinition definition)
        {
            Result<ProductName> productName = ProductName.Create(definition.Name);
            Result<ManufacturerName> manufacturer = ManufacturerName.Create(definition.Manufacturer);
            Result<Maybe<Email>> emailOrNothing = GetImporterEmail(definition);

            return Result.Combine(productName, manufacturer, emailOrNothing)
                .OnSuccess(() => new Product
                {
                    ProductId = definition.ProductId,
                    Category = definition.Category,
                    Name = productName.Value,
                    Manufacturer = manufacturer.Value,
                    ImporterEmail = emailOrNothing.Value,
                    Quantity = definition.Quantity
                })
                .OnSuccess(p => _repository.Add(p))
                .OnBoth(r => r.IsSuccess ? Commit() : Response.BadRequest(r.Error));
        }

        private static Result<Maybe<Email>> GetImporterEmail(ProductDefinition definition)
        {
            if (definition.ImporterEmail == null)
            {
                return Result.Ok(new Maybe<Email>());
            }
            return Email.Create(definition.ImporterEmail)
                .Map(x => (Maybe<Email>)x);
        }

        public HttpResponse GetProduct(int productId)
        {
            return _repository.Find(productId)
                .ToResult($"Product with id {productId} was not found")
                .OnSuccess(product =>
                {
                    Maybe<Email> importerEmail = product.ImporterEmail;
                    return new ProductDefinition
                    {
                        ProductId = product.ProductId,
                        Category = product.Category,
                        Name = product.Name.Value,
                        Manufacturer = product.Manufacturer.Value,
                        ImporterEmail = importerEmail.HasValue ? importerEmail.Value.Value : null,
                        Quantity = product.Quantity
                    };
                })
                .OnBoth(t => t.IsSuccess ? Response.Ok(t.Value) : Response.BadRequest(t.Error));
        }

        public HttpResponse Order(int productId, uint quantity)
        {
            try // models an application level try catch to convert unexpected exceptions to 5XX
            {
                Maybe<Product> maybe = _repository.Find(productId);

                Result<Product> result = maybe
                    .ToResult($"Product with id {productId} was not found");

                Result<Product> ensure = result
                    .Ensure(_ => quantity <= (uint)Constants.MaxQuantityInOrder, "The order is too large");

                Result<Product> onSuccess = ensure
                    .OnSuccess(p => p.Quantity < quantity ? OrderFromSupplier(p, quantity) : Result.Ok(p));

                Result<uint> success = onSuccess
                    .OnSuccess(p => p.Quantity -= quantity);

                HttpResponse httpResponse = success
                    .OnBoth(t => t.IsSuccess ? Commit() : Response.BadRequest(t.Error));

                return httpResponse;
            }
            catch (Exception e)
            {
                return Response.InternalError(e.Message);
            }
        }

        private Result<Product> OrderFromSupplier(Product product, uint quantity)
        {
            uint excess = quantity - product.Quantity;

            Result<uint> ensure = OrderFromSupplierCore(product, excess)
                .Ensure(orderedQuantity => product.Quantity + orderedQuantity >= quantity, "The product is out of stock");

            Result<uint> onSuccess = ensure
                .OnSuccess(orderedQuantity => product.Quantity += orderedQuantity);

            Result<Product> orderFromSupplier = onSuccess
                .OnSuccess(_ => product);

            return orderFromSupplier;
        }

        private Result<uint> OrderFromSupplierCore(Product product, uint excess)
        {
            // Don't catch. Allow to propagate (or catch and rethrow if to want to log or add more information)
            uint ordered = _supplier.Order(product.ProductId, product.Manufacturer, excess);
            return Result.Ok(ordered);
        }


        private HttpResponse Commit()
        {
            try // The try catch needs to be moved down to _repository.Commit(), catch exceptions on the lower level possible
            {
                _repository.Commit();
                return Response.Ok();
            }
            catch (Exception ex)
            {
                return Response.InternalError(ex.Message);
            }
        }
    }
}