using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TCJ.AspNetCore.Results;
using TCJ.Core.Results;

namespace TCJ.AspNetCore.Tests;

public sealed class ResultHttpExtensionsTests
{
    [Fact]
    public void Successful_value_maps_to_ok()
    {
        IResult httpResult = Result.Success("value").ToHttpResult();

        IStatusCodeHttpResult statusCodeResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(httpResult);
        IValueHttpResult valueResult = Assert.IsAssignableFrom<IValueHttpResult>(httpResult);

        Assert.Equal(StatusCodes.Status200OK, statusCodeResult.StatusCode);
        Assert.Equal("value", valueResult.Value);
    }

    [Fact]
    public void Not_found_error_maps_to_problem_details()
    {
        IResult httpResult = Result.Failure<string>(error: CommonErrors.NotFound("Product", id: 10)).ToHttpResult();

        IStatusCodeHttpResult statusCodeResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(httpResult);
        IValueHttpResult valueResult = Assert.IsAssignableFrom<IValueHttpResult>(httpResult);
        ProblemDetails problem = Assert.IsType<ProblemDetails>(valueResult.Value);

        Assert.Equal(StatusCodes.Status404NotFound, statusCodeResult.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Equal("The requested resource was not found.", problem.Title);
        Assert.True(problem.Extensions.ContainsKey("errors"));
    }

    [Fact]
    public void Validation_errors_map_to_validation_problem_details()
    {
        IResult httpResult = Result.Failure(
            [
                CommonErrors.ValidationForField("Name", message: "Name is required."),
                CommonErrors.ValidationForField("Name", message: "Name is too short."),
            ]).ToHttpResult();

        IStatusCodeHttpResult statusCodeResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(httpResult);
        IValueHttpResult valueResult = Assert.IsAssignableFrom<IValueHttpResult>(httpResult);
        HttpValidationProblemDetails problem = Assert.IsType<HttpValidationProblemDetails>(valueResult.Value);

        Assert.Equal(StatusCodes.Status400BadRequest, statusCodeResult.StatusCode);
        Assert.Equal(new[] { "Name is required.", "Name is too short." }, problem.Errors["Name"]);
        Assert.True(problem.Extensions.ContainsKey("errorCodes"));
    }
}
